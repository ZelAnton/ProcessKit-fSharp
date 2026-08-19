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

    /// Whether the system `cmd.exe` above is actually present — the capability snapshot's honest answer for
    /// the `.cmd`/`.bat` shim host, read from the very path the wrapper would launch (never `PATH`). Probed
    /// on each call rather than cached: it is a point-in-time observation, like every other fact the
    /// snapshot reports. An unreadable/denied system directory is simply "not available here" — the same
    /// answer the launch itself would end at.
    let systemCmdExeAvailable () : bool =
        try
            File.Exists systemCmdExe.Value
        with _ ->
            // A denied or torn-down system directory is not a usable shim host; a probe must not throw.
            false

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
    /// (T-181), the prefer-local search (T-182), and the effective-child-`PATH` substitution (T-339). A
    /// bare name whose only match under our own PATHEXT-aware resolver (`Common.resolveProgram`/`probeDir`,
    /// reused via `resolveWindowsLaunch` — no second copy) carries a non-`.exe` extension is launched via
    /// that resolved absolute path instead of the bare name, because the OS's own bare-name `PATH` search
    /// appends only `.exe` and would miss it — the `which`-vs-spawn divergence this closes. A prefer-local
    /// match (`Command.PreferLocal`) is searched first and is likewise substituted by absolute path (even a
    /// `.exe`, since the OS never searches those directories). So is EVERY match, `.exe` included, once the
    /// command overrides or clears the child's `PATH`: the OS's own search reads the PARENT's `PATH`, so
    /// deferring to it there would launch a same-named executable the caller's config never named. A
    /// `.cmd`/`.bat` match — on `PATH`, prefer-local, or an overridden `PATH` — additionally routes through
    /// `cmd.exe /d /c` with BatBadBut-safe quoting. A relative path-form program is first anchored to
    /// `CurrentDir` and substituted as an absolute path, keeping Windows aligned with POSIX child-side
    /// chdir resolution. Against an unchanged child `PATH`, a `PATH` `.exe` match and a name that resolves
    /// to nothing both stay verbatim, so the OS's richer search is preserved exactly as before. Fails when
    /// a batch-wrapper argument (or script path) cannot be safely quoted for cmd.exe, and — for an
    /// overridden child `PATH` that holds no such program — with the same `ProcessError.NotFound` a
    /// `Command.ResolveProgram` preflight of this config reports, before any native spawn is attempted.
    let private buildWindowsCommandLine (command: Command) : Result<string, ProcessError> =
        let appendRaw (quoted: string) =
            if command.Config.WindowsRawArgs.IsEmpty then
                quoted
            else
                quoted + " " + String.Join(" ", command.Config.WindowsRawArgs)

        match resolveWindowsLaunch command with
        | Error error -> Error error
        | Ok WindowsLaunch.AsIs ->
            let parts = command.Program :: List.ofSeq command.Config.Args
            Ok(parts |> List.map quoteWindowsArg |> String.concat " " |> appendRaw)
        | Ok(WindowsLaunch.DirectPath resolved) ->
            let parts = resolved :: List.ofSeq command.Config.Args
            Ok(parts |> List.map quoteWindowsArg |> String.concat " " |> appendRaw)
        | Ok(WindowsLaunch.BatchWrapper resolved) ->
            if command.Config.WindowsRawArgs.IsEmpty then
                buildBatchCommandLine command.Program resolved (List.ofSeq command.Config.Args)
            else
                Error(
                    ProcessError.Unsupported
                        "WindowsRawArg with an automatically resolved .cmd/.bat program; invoke cmd.exe explicitly so the raw fragment's parser and position are unambiguous"
                )

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

    // Test seam: observe the exact creation flags computed at the native boundary. The boolean identifies
    // the ConPTY path (`true`) versus the regular contained path (`false`). Detached spawn deliberately does
    // not participate: its separate flag branch is outside this seam and outside ConPTY isolation.
    let mutable windowsCreationFlagsObserverForTests: (bool * uint32 -> unit) option =
        None

    // Test seam for the separate public-signal registration bit stored on Spawned. Creation flags and
    // targetability intentionally differ for a default ConPTY child, so tests observe both decisions.
    let mutable windowsCtrlGroupObserverForTests: (bool * bool -> unit) option = None

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

    // Test seam: `TerminateJobObject`, overridable so a fault-injection test can force a REFUSED job
    // terminate (access denied / an invalid handle) deterministically — a Job we hold a TERMINATE right
    // to cannot be made to refuse on demand. A hook that models a failure sets the Win32 error the
    // production classification reads back with `Marshal.SetLastPInvokeError`. Production always runs the
    // real entry point; only the (sequential) tests reassign it, and restore it in a `finally`.
    let mutable terminateJobObjectHook: nativeint -> uint32 -> bool =
        fun job exitCode -> TerminateJobObject(job, exitCode)

    [<DllImport("kernel32.dll", SetLastError = true)>]
    extern bool private TerminateProcess(nativeint hProcess, uint32 uExitCode)

    // Test seam: `TerminateProcess`, on the same terms as `terminateJobObjectHook` above — a child whose
    // handle we opened with full access never refuses termination, so the honest-failure branch is only
    // reachable by injection. It covers ONLY the kill verbs' `terminateWindowsProcess`; the spawn-rollback
    // `TerminateProcess` calls run the real entry point directly, so a test that forces a kill failure can
    // never leave a half-spawned child alive.
    let mutable terminateProcessHook: nativeint -> uint32 -> bool =
        fun hProcess exitCode -> TerminateProcess(hProcess, exitCode)

    // `lpCommandLine` is a `nativeint` pointer to a WRITABLE unmanaged buffer, NOT a managed `string`.
    // `CreateProcessW` may modify `lpCommandLine` in place while probing executable candidates (Win32
    // documents this), so passing a marshalled `string` (pinned, not copied, under `CharSet.Unicode`)
    // would let the OS write into a managed string's memory — a possibly interned literal shared
    // process-wide (T-198). The call site hands over a private `Marshal.StringToHGlobalUni` copy and frees
    // it after the call.
    [<DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)>]
    extern bool private CreateProcessW(
        nativeint lpApplicationName,
        nativeint lpCommandLine,
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

    // `GetExitCodeProcess`'s "this process has not exited" sentinel. It is deliberately NOT trusted on its
    // own below: 259 is also a perfectly legal exit code a child can call `ExitProcess(259)` with, so it
    // answers "still running OR exited with 259" — the classic Win32 ambiguity. The process handle's own
    // signalled state resolves it (a process object signals on exit and never un-signals).
    [<Literal>]
    let private STILL_ACTIVE = 259u

    [<Literal>]
    let private WAIT_OBJECT_0 = 0u

    [<Literal>]
    let private WAIT_TIMEOUT = 0x102u

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

    // The write half of `GetStdHandle`: replaces one of this process's own standard-handle slots. Used by
    // exactly one caller — the headless ConPTY launcher's temporary null swap (`withNulledLauncherStdio`,
    // in the ConPTY section below) — because the value it writes is PROCESS-GLOBAL state, not per-spawn
    // state. `EntryPoint` is spelled out because an F# `extern` otherwise resolves the export by the F#
    // function name and a mismatch would only surface as an `EntryPointNotFoundException` at the first
    // call (K-136).
    [<DllImport("kernel32.dll", SetLastError = true, EntryPoint = "SetStdHandle")>]
    extern bool private SetStdHandle(int nStdHandle, nativeint handle)

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

    // Test seam: the `CloseHandle` behind `closeHandleIfValid` — and only that route; the direct
    // `CloseHandle` calls on a child's process/thread handles stay real. Overridable so a
    // fault-injection test can count the closes of one handle VALUE and make a cleanup step throw:
    // a genuine double-close is not observable from managed code (Win32 reports the second
    // `CloseHandle` as an ordinary success on whatever object has since taken the recycled value),
    // so the seam is the only way to assert it never happens. Production always runs the real entry
    // point; only the (sequential) tests reassign it, and restore it in a `finally`.
    let mutable closeHandleIfValidHook: nativeint -> bool = CloseHandle

    let private closeHandleIfValid (handle: nativeint) =
        if isValidHandle handle then
            closeHandleIfValidHook handle |> ignore

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
    ///
    /// This same flag is ALSO what makes `Command.KillOnParentDeath` a no-op on Windows (verified, not
    /// assumed): the parent process owns the only handle to this Job, and when the parent dies for ANY
    /// reason — including a hard `TerminateProcess` or a crash, where no managed `Dispose`/finalizer runs
    /// — the kernel closes all of that process's handles during rundown. Closing the last Job handle then
    /// terminates every process in the Job, so a sudden parent death already reaps the whole tree with no
    /// extra opt-in (`KillOnParentDeathScope.WholeTree`). The guarantee holds as long as the Job's handles
    /// live only in the parent (they do: the only other handle is `duplicateJobHandle`'s short-lived
    /// graceful-teardown duplicate, also owned by the parent).
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

    // `terminateWindowsJob` lives further down, next to `jobTreeAliveWindows`: classifying a REFUSED job
    // terminate needs that liveness read, and F# resolves declarations strictly top-to-bottom.

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

    /// Has this process provably concluded? Answered through the handle the CALLER already owns — never
    /// through a pid, so it can neither race a recycle nor need a fresh open. `Some true` = provably
    /// exited, `Some false` = provably still running, `None` = the OS would not say (the handle lacks
    /// query rights, or is no longer usable), which callers must treat as "unknown", never as "dead".
    ///
    /// Two questions, because neither alone is conclusive. `GetExitCodeProcess` answers with a code, but
    /// its `STILL_ACTIVE` (259) sentinel collides with a legitimate `ExitProcess(259)`; a zero-timeout wait
    /// on the process object disambiguates exactly that case, because the object signals on exit and stays
    /// signalled. Any code other than 259 is already unambiguous and needs no wait.
    let private processHasExitedWindows (hProcess: nativeint) : bool option =
        let mutable exitCode = 0u

        if not (GetExitCodeProcess(hProcess, &exitCode)) then
            None
        elif exitCode <> STILL_ACTIVE then
            Some true
        else
            match WaitForSingleObject(hProcess, 0u) with
            | WAIT_OBJECT_0 -> Some true // signalled: it really did exit, with code 259
            | WAIT_TIMEOUT -> Some false // not signalled: genuinely still running
            | _ -> None // WAIT_FAILED (or an abandoned-mutex answer that cannot apply here)

    /// Hard-kill one Windows process (not its descendants — for that, terminate the whole Job). Returns
    /// what the OS actually did, never a fabricated success: `TerminateProcess` can be refused (access
    /// denied, an unusable handle), and reporting that as `Ok` would tell a caller a live process is dead.
    ///
    /// A refusal is classified through the process handle we were handed, not through the Win32 error
    /// number: a target that has ALREADY exited is an idempotent no-op success (killing a corpse is what
    /// the caller wanted, and racing the child's own exit is not a caller error), while a target still
    /// running — or whose state the OS will not disclose — is an honest `ProcessError.Io`. Error numbers
    /// alone cannot make that call: `ERROR_ACCESS_DENIED` is returned BOTH for a process that is already
    /// terminating and for one we simply may not kill. This mirrors the Rust prototype's fix
    /// (`1bd19ff53697`), which suppresses the failure only for a provably concluded process.
    ///
    /// The last-error read happens BEFORE the classification probe, since that probe issues its own
    /// P/Invokes and would otherwise overwrite the code being reported.
    let terminateWindowsProcess (hProcess: nativeint) : Result<unit, ProcessError> =
        if terminateProcessHook hProcess 1u then
            Ok()
        else
            let message = Win32Exception(Marshal.GetLastWin32Error()).Message

            match processHasExitedWindows hProcess with
            | Some true -> Ok()
            | Some false -> Error(ProcessError.Io $"failed to terminate the process (it is still running): {message}")
            | None ->
                Error(
                    ProcessError.Io
                        $"failed to terminate the process, and its handle could not confirm that it had exited: {message}"
                )

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

    // ----------------------------------------------------------------------------------
    // Windows: console output code page
    // ----------------------------------------------------------------------------------
    //
    // A Windows console program that predates UTF-8 does not write UTF-8: it writes a CODE PAGE, and
    // which one depends on the console it was given. Both entry points are needed because they answer
    // different questions and either can be the only meaningful answer. `GetConsoleOutputCP` reports
    // the code page of the console attached to THIS process — the one a child inherits, and the one a
    // `chcp` in that console changes at runtime — but it returns 0 when the process has no console at
    // all (a GUI app, a Windows service, a test host started detached). `GetOEMCP` reports the system
    // OEM code page, cannot fail, and is what a console child's C runtime falls back to when it has no
    // console of its own.
    //
    // Neither declares `SetLastError`, unlike the rest of this file: `GetOEMCP` has no documented
    // failure mode, and the only `GetConsoleOutputCP` outcome this layer distinguishes is its
    // documented 0 sentinel, which is answered by the OEM fallback rather than surfaced as a Win32
    // message. There is nothing here for a `GetLastError` read to add.

    [<DllImport("kernel32.dll")>]
    extern uint32 private GetConsoleOutputCP()

    [<DllImport("kernel32.dll")>]
    extern uint32 private GetOEMCP()

    /// The code page a legacy console child of this process writes its output in: the output code page
    /// of this process's console, or the system OEM code page when this process has no console. Read
    /// live on every call rather than cached — a `chcp` in the console changes the answer while the
    /// process runs.
    let consoleOutputCodePage () : int =
        match GetConsoleOutputCP() with
        | 0u -> int (GetOEMCP())
        | codePage -> int codePage

    // Tree introspection / suspend-resume for the `process-control` surface.
    [<Literal>]
    let private JobObjectBasicProcessIdList = 3

    [<Literal>]
    let private PROCESS_SUSPEND_RESUME = 0x0800u

    // Least-privilege addition to the suspend/resume handle so the same handle can also be used
    // for the `IsProcessInJob` re-check below, without a second `OpenProcess` call.
    [<Literal>]
    let private PROCESS_QUERY_LIMITED_INFORMATION = 0x1000u

    // The documented process-query right required by GetProcessTimes, GetProcessIoCounters, and
    // GetProcessMemoryInfo. Per-member sampling opens a short-lived handle with query rights only — no
    // terminate, VM-write, or handle-inheritance capability.
    [<Literal>]
    let private PROCESS_QUERY_INFORMATION = 0x0400u

    // The two access rights `AssignProcessToJobObject` requires on the target process handle: it must be
    // able to set the process's quota (Job limits are quotas) and to terminate it (the Job owns its
    // lifetime once assigned). `adoptIntoJob` opens a foreign process with exactly these (plus
    // PROCESS_QUERY_LIMITED_INFORMATION for the `IsProcessInJob` disambiguation on failure) — no more.
    [<Literal>]
    let private PROCESS_TERMINATE = 0x0001u

    [<Literal>]
    let private PROCESS_SET_QUOTA = 0x0100u

    // Win32 error code distinguished when an adopt fails, so the typed error can name the real cause
    // rather than a bare number: the caller lacks rights to the foreign process, or an assign was refused
    // because the process is already in a Job that does not allow nesting on this OS configuration.
    // (ERROR_INVALID_PARAMETER — the "pid does not exist" case — is already defined once above.)
    [<Literal>]
    let private ERROR_ACCESS_DENIED = 5

    /// Test seam (internal, not public API): replaces the two query-handle OpenProcess attempts used by
    /// per-member sampling. The result carries a Win32 error code on failure so tests can distinguish a
    /// proven missing pid (`ERROR_INVALID_PARAMETER`) from an inaccessible live member.
    let mutable openMemberProcessForTests: (uint32 -> int -> Result<nativeint, int>) option =
        None

    /// Test seam (internal, not public API): replaces the `IsProcessInJob` membership question — the calls
    /// made around a member's resource read, and the one `postCloseToJobWindows` asks before every
    /// WM_CLOSE. Production leaves it `None`; tests use it with a synthetic handle so the
    /// exit-after-pre-read and same-Job identity checks can be driven without a real PID-reuse race.
    let mutable isProcessInJobForTests: (nativeint -> nativeint -> bool) option = None

    /// Test seam (internal, not public API): replaces the `OpenProcess` call `adoptIntoJob` makes for the
    /// foreign process it is about to assign. The result carries a Win32 error code on failure, so the two
    /// refusals a real adopt must classify apart — `ERROR_ACCESS_DENIED` (another user, a higher integrity
    /// level, a protected process) and `ERROR_INVALID_PARAMETER` (the pid names nothing) — can be driven
    /// deterministically, instead of pointing a test at a real protected process it must never actually
    /// place in a kill-on-close Job. Mirrors `openMemberProcessForTests`. Production leaves it `None`.
    let mutable adoptOpenProcessForTests: (int -> Result<nativeint, int>) option = None

    /// Test seam (internal, not public API): replaces the `OpenProcess` call the suspend/resume walk makes
    /// for each Job member. The result carries a Win32 error code on failure, so a test can drive the
    /// "proven gone" (`ERROR_INVALID_PARAMETER`) and "refused for some other reason" classifications apart
    /// without racing a real member exit or pid recycle. Production leaves it `None`.
    let mutable openControlHandleForTests: (int -> Result<nativeint, int>) option = None

    /// Test seam (internal, not public API): replaces `GetProcessTimes` for a synthetic member handle.
    /// The tuple is `(creation, exit, kernel, user)` in Windows' 100-nanosecond ticks. A test can return
    /// one stable creation time followed by a non-zero exit time or a different creation time to model
    /// the two identity failures the production post-read gate must reject.
    let mutable getProcessTimesForTests: (nativeint -> (int64 * int64 * int64 * int64) option) option =
        None

    /// Test seam (internal, not public API): replaces the pre-sampling Windows process-identity snapshot.
    /// The map is keyed by PID and contains the kernel creation-time token. Tests can mutate the token
    /// after this hook returns, before `OpenProcess`, to model same-Job PID reuse in that exact window.
    let mutable processIdentitySnapshotForTests: (unit -> Map<int, int64> option) option =
        None

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

    [<StructLayout(LayoutKind.Sequential)>]
    type private FILETIME_NATIVE =
        struct
            val mutable LowDateTime: uint32
            val mutable HighDateTime: uint32
        end

    [<StructLayout(LayoutKind.Sequential)>]
    type private PROCESS_MEMORY_COUNTERS =
        struct
            val mutable cb: uint32
            val mutable PageFaultCount: uint32
            val mutable PeakWorkingSetSize: unativeint
            val mutable WorkingSetSize: unativeint
            val mutable QuotaPeakPagedPoolUsage: unativeint
            val mutable QuotaPagedPoolUsage: unativeint
            val mutable QuotaPeakNonPagedPoolUsage: unativeint
            val mutable QuotaNonPagedPoolUsage: unativeint
            val mutable PagefileUsage: unativeint
            val mutable PeakPagefileUsage: unativeint
        end

    [<DllImport("kernel32.dll", SetLastError = true)>]
    extern bool private GetProcessTimes(
        nativeint hProcess,
        FILETIME_NATIVE& lpCreationTime,
        FILETIME_NATIVE& lpExitTime,
        FILETIME_NATIVE& lpKernelTime,
        FILETIME_NATIVE& lpUserTime
    )

    [<DllImport("kernel32.dll", SetLastError = true)>]
    extern bool private GetProcessIoCounters(nativeint hProcess, IO_COUNTERS& lpIoCounters)

    [<DllImport("psapi.dll", SetLastError = true)>]
    extern bool private GetProcessMemoryInfo(nativeint Process, PROCESS_MEMORY_COUNTERS& ppsmemCounters, uint32 cb)

    // `NtQuerySystemInformation(SystemProcessInformation)` exposes the process creation token without
    // opening a per-process handle. It is the identity source for protected processes, which may reject
    // both query-handle opens and `GetProcessTimes`; the Job PID list itself carries no generation data.
    [<Literal>]
    let private SystemProcessInformation = 5

    [<DllImport("ntdll.dll")>]
    extern int private NtQuerySystemInformation(
        int systemInformationClass,
        nativeint systemInformation,
        uint32 systemInformationLength,
        uint32& returnLength
    )

    // NtSuspendProcess/NtResumeProcess freeze/thaw every thread of a process in one call. They are
    // undocumented ntdll entry points but stable and the standard way to suspend a whole process;
    // the documented alternative (snapshot every thread + SuspendThread) is far more code.
    [<DllImport("ntdll.dll")>]
    extern int private NtSuspendProcess(nativeint hProcess)

    [<DllImport("ntdll.dll")>]
    extern int private NtResumeProcess(nativeint hProcess)

    // Test seams: the two entry points the suspend/resume walk delivers through, overridable so a
    // fault-injection test can return a chosen NTSTATUS deterministically — a genuinely failing suspend or
    // resume on a live member of our own job cannot be provoked on a healthy host, and that failure is
    // exactly what must reach the caller as `ProcessError.Io`. Production always runs the real entry
    // points; only the (sequential) tests reassign these, and restore them in a `finally`. Mirrors the
    // existing `resumeThreadHook` fault seam above.
    let mutable suspendProcessHook: nativeint -> int = NtSuspendProcess

    let mutable resumeProcessHook: nativeint -> int = NtResumeProcess

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

    [<Literal>]
    let private STATUS_SUCCESS = 0

    // NtQuerySystemInformation returns this NTSTATUS when the caller's buffer is too small.
    [<Literal>]
    let private STATUS_INFO_LENGTH_MISMATCH = -1073741820

    // 0xC000010A. The kernel's suspend/resume primitive answers with it for a process that has already
    // begun (or finished) exiting while its process object is still referenced — the routine benign
    // outcome for the suspend/resume walk, because a member can leave between the Job snapshot and the
    // call, and a process on its way out has nothing left to freeze or thaw. It is the Windows shape of
    // the POSIX "target gone" (ESRCH) no-op that `Suspend`/`Resume` already document as a success.
    [<Literal>]
    let private STATUS_PROCESS_IS_TERMINATING = -1073741558

    [<Literal>]
    let private initialProcessIdentityBufferSize = 1024 * 1024

    [<Literal>]
    let private maxIdentityQueryAttempts = 8

    // SYSTEM_PROCESS_INFORMATION is a variable-length linked record. The fields needed here are stable
    // across supported Windows versions: CreateTime is at byte 32, while UniqueProcessId follows the
    // pointer-sized UNICODE_STRING and BasePriority fields (offset 80 on x64, 68 on x86).
    let private systemProcessIdOffset = if IntPtr.Size = 8 then 80 else 68

    let private readNativeProcessId (buffer: nativeint) (offset: int) : int option =
        let value =
            if IntPtr.Size = 8 then
                Marshal.ReadInt64(buffer, offset)
            else
                int64 (Marshal.ReadInt32(buffer, offset))

        if value > 0L && value <= int64 Int32.MaxValue then
            Some(int value)
        else
            None

    let private systemProcessIdentitySnapshot () : Map<int, int64> option =
        match processIdentitySnapshotForTests with
        | Some hook -> hook ()
        | None ->
            let rec query (bufferSize: int) (attempt: int) : Map<int, int64> option =
                let buffer = Marshal.AllocHGlobal bufferSize

                try
                    let mutable returnLength = 0u

                    let status =
                        NtQuerySystemInformation(SystemProcessInformation, buffer, uint32 bufferSize, &returnLength)

                    if status = STATUS_SUCCESS then
                        let reportedLength =
                            if returnLength > 0u && returnLength <= uint32 bufferSize then
                                int returnLength
                            else
                                bufferSize

                        let minimumRecordSize = systemProcessIdOffset + IntPtr.Size
                        let mutable offset = 0
                        let mutable finished = false
                        let mutable valid = true
                        let identities = ResizeArray<int * int64>()

                        while not finished && valid do
                            if offset < 0 || offset + minimumRecordSize > reportedLength then
                                valid <- false
                            else
                                let nextOffset = Marshal.ReadInt32(buffer, offset)

                                if nextOffset < 0 || (nextOffset <> 0 && nextOffset < minimumRecordSize) then
                                    valid <- false
                                else
                                    match readNativeProcessId buffer (offset + systemProcessIdOffset) with
                                    | Some pid ->
                                        let creationTime = Marshal.ReadInt64(buffer, offset + 32)

                                        if creationTime > 0L then
                                            identities.Add(pid, creationTime)
                                    | None -> ()

                                    if nextOffset = 0 then
                                        finished <- true
                                    elif offset > reportedLength - nextOffset then
                                        valid <- false
                                    else
                                        offset <- offset + nextOffset

                        if valid && finished then
                            Some(Map.ofSeq identities)
                        else
                            None
                    elif status = STATUS_INFO_LENGTH_MISMATCH && attempt < maxIdentityQueryAttempts then
                        let required =
                            if returnLength > uint32 bufferSize then
                                int returnLength + 4096
                            else
                                bufferSize * 2

                        query (min (16 * 1024 * 1024) required) (attempt + 1)
                    else
                        None
                finally
                    Marshal.FreeHGlobal buffer

            query initialProcessIdentityBufferSize 1

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

    let private fileTimeTicks (value: FILETIME_NATIVE) : int64 =
        let combined = (uint64 value.HighDateTime <<< 32) ||| uint64 value.LowDateTime

        if combined > uint64 Int64.MaxValue then
            Int64.MaxValue
        else
            int64 combined

    let private saturatingAdd (left: int64) (right: int64) =
        if right > 0L && left > Int64.MaxValue - right then
            Int64.MaxValue
        else
            left + right

    let private isProcessInJob (handle: nativeint) (job: nativeint) : bool =
        match isProcessInJobForTests with
        | Some hook -> hook handle job
        | None ->
            let mutable stillMember = false
            IsProcessInJob(handle, job, &stillMember) && stillMember

    let private readProcessTimes (handle: nativeint) : (int64 * int64 * int64 * int64) option =
        match getProcessTimesForTests with
        | Some hook -> hook handle
        | None ->
            let mutable creation = FILETIME_NATIVE()
            let mutable exit = FILETIME_NATIVE()
            let mutable kernel = FILETIME_NATIVE()
            let mutable user = FILETIME_NATIVE()

            if GetProcessTimes(handle, &creation, &exit, &kernel, &user) then
                Some(fileTimeTicks creation, fileTimeTicks exit, fileTimeTicks kernel, fileTimeTicks user)
            else
                None

    /// Read one process's resources through a short-lived query-only process handle. The expected creation
    /// token was captured before the Job PID snapshot and before this handle was opened, so a PID reused by
    /// a different process in the gap is rejected before any metric is trusted. The handle is checked
    /// against the Job before and after the metric read, while `GetProcessTimes` supplies the same stable
    /// process identity and exit-state checks on both sides.
    let private readMemberStatsFromHandle
        (pid: int)
        (job: nativeint)
        (expectedIdentity: int64)
        (handle: nativeint)
        : MemberStats option =
        if not (isProcessInJob handle job) then
            None
        else
            match readProcessTimes handle with
            | None -> None
            | Some(creation, exit, _, _) when creation <= 0L || creation <> expectedIdentity || exit <> 0L -> None
            | Some(creation, _, kernel, user) ->
                let cpu =
                    let total = saturatingAdd user kernel
                    Some(TimeSpan.FromTicks total)

                let residentMemory =
                    let mutable counters = PROCESS_MEMORY_COUNTERS()
                    counters.cb <- uint32 (Marshal.SizeOf<PROCESS_MEMORY_COUNTERS>())

                    if GetProcessMemoryInfo(handle, &counters, counters.cb) then
                        let bytes = uint64 counters.WorkingSetSize

                        if bytes > uint64 Int64.MaxValue then
                            Some Int64.MaxValue
                        else
                            Some(int64 bytes)
                    else
                        None

                let ioCounters =
                    let mutable io = IO_COUNTERS()

                    if GetProcessIoCounters(handle, &io) then
                        let count (value: uint64) =
                            if value > uint64 Int64.MaxValue then
                                Int64.MaxValue
                            else
                                int64 value

                        Some
                            { ReadBytes = count io.ReadTransferCount
                              WriteBytes = count io.WriteTransferCount
                              ReadOperations = count io.ReadOperationCount
                              WriteOperations = count io.WriteOperationCount }
                    else
                        None

                // The process handle keeps referring to the same kernel process object, so a changed
                // creation time is a deterministic identity failure rather than a new member. A non-zero
                // exit time proves the original member ended after the pre-read, even though the handle
                // itself remains valid; the second Job check closes the membership race as well.
                match readProcessTimes handle with
                | Some(postCreation, postExit, _, _) when
                    postCreation = expectedIdentity
                    && postCreation = creation
                    && postExit = 0L
                    && isProcessInJob handle job
                    ->
                    Some(MemberStats(pid, cpu, residentMemory, ioCounters))
                | _ -> None

    /// The three-way verdict of trying to open a query handle to a pid — `ERROR_INVALID_PARAMETER` is the
    /// one honest "no such process" answer; any other open failure means the process exists but this
    /// caller may not inspect it. Not `private`: the standalone `ProcessLookup.processInfo` query
    /// (`processInfo` below) reuses this SAME classification, so a group member's "gone vs inaccessible"
    /// verdict and an arbitrary external pid's can never disagree.
    type MemberProcessOpen =
        | Opened of nativeint
        | Inaccessible
        | Gone

    /// Open a query-only handle to `pid`, classified into `MemberProcessOpen`. Not `private`: shared by
    /// `readMemberStatsForPidsWithIdentities` (a known Job member) and `processInfo` below (an arbitrary
    /// external pid) — the one primitive that answers "does this pid exist, and may I inspect it" for
    /// both.
    let openMemberProcess (pid: int) : MemberProcessOpen =
        let openWith access =
            match openMemberProcessForTests with
            | Some opener -> opener access pid
            | None ->
                let handle = OpenProcess(access, false, uint32 pid)

                if handle = IntPtr.Zero then
                    Error(Marshal.GetLastWin32Error())
                else
                    Ok handle

        let fullQuery =
            openWith (PROCESS_QUERY_INFORMATION ||| PROCESS_QUERY_LIMITED_INFORMATION)

        match fullQuery with
        | Ok handle -> Opened handle
        | Error ERROR_INVALID_PARAMETER -> Gone
        | Error _ ->
            // A protected process can deny the broad query right while still allowing the limited right.
            // Try the narrower handle before classifying the member as inaccessible.
            match openWith PROCESS_QUERY_LIMITED_INFORMATION with
            | Ok handle -> Opened handle
            | Error ERROR_INVALID_PARAMETER -> Gone
            | Error _ -> Inaccessible

    /// Identity + best-effort metadata for an **arbitrary** pid — the Windows backend of the standalone
    /// `ProcessLookup.processInfo` query (T-385), for a pid the caller holds outside any Job/group.
    ///
    /// `openMemberProcess` — the SAME primitive `readMemberStats` already opens each known Job member
    /// with — is the existence-AND-permission oracle: `Gone` (`ERROR_INVALID_PARAMETER`) is the honest
    /// "no such process" negative; `Inaccessible` (any other open failure — a protected/higher-integrity
    /// process such as an anti-malware PPL, or the `System` process) means the pid may well exist but this
    /// caller may not inspect it, reported as `ProcessError.Io` and never folded into "gone". Once the
    /// handle proves the pid is queryable it is closed immediately, and the enrichment itself goes through
    /// the SAME whole-system Toolhelp32 snapshot `readMembersInfo` uses to enrich a Job's members — no
    /// second, parallel identity-reading mechanism for this entry point — so a pid that exits in the
    /// narrow window between the two reads is honestly `Ok None` (it just vanished), never a fabricated
    /// record.
    let processInfo (pid: int) : Result<MemberInfo option, ProcessError> =
        match openMemberProcess pid with
        | Gone -> Ok None
        | Inaccessible ->
            Error(
                ProcessError.Io
                    $"pid {pid} could not be inspected: OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION) was denied — a protected/higher-integrity process, or another user's, never read this as \"gone\""
            )
        | Opened handle ->
            closeWindowsHandle handle

            match readMembersInfo [ pid ] with
            | [ info ] -> Ok(Some info)
            | _ ->
                // Exited in the narrow window between the confirmed-open handle above and the Toolhelp32
                // snapshot `readMembersInfo` takes — an honest "gone", not a fabricated record.
                Ok None

    /// Sample a supplied Job-member snapshot with a safe query-only handle. `identities` is captured before
    /// the member PID list, so it remains the generation ledger even when a pid is reused before
    /// `OpenProcess`. An inaccessible pid is retained only when a fresh Job query AND a fresh system
    /// identity snapshot still match the captured generation; a missing identity is fail-closed.
    let private readMemberStatsForPidsWithIdentities
        (job: nativeint)
        (identities: Map<int, int64>)
        (pids: int list)
        : MemberStats list =
        let stats = ResizeArray<MemberStats>()

        for pid in pids do
            match Map.tryFind pid identities with
            | None -> ()
            | Some expectedIdentity ->
                match openMemberProcess pid with
                | Opened handle ->
                    try
                        match readMemberStatsFromHandle pid job expectedIdentity handle with
                        | Some value -> stats.Add value
                        | None -> ()
                    finally
                        CloseHandle handle |> ignore
                | Inaccessible ->
                    // ACCESS_DENIED proves only that metrics cannot be queried. Refresh both the Job
                    // membership and the process-generation snapshot before retaining a None-metric row.
                    // This keeps a protected PID from being mistaken for a same-number replacement.
                    match membersWindows job, systemProcessIdentitySnapshot () with
                    | Ok current, Some currentIdentities when
                        current |> List.contains pid
                        && Map.tryFind pid currentIdentities = Some expectedIdentity
                        ->
                        stats.Add(MemberStats(pid, None, None, None))
                    | _ ->
                        // A failed refresh, a changed generation, or a pid absent from the fresh Job
                        // snapshot is not a confirmed current member, so fail closed.
                        ()
                | Gone ->
                    // ERROR_INVALID_PARAMETER proves that the process no longer exists between
                    // enumeration and opening, so the vanished member is omitted.
                    ()

        List.ofSeq stats

    /// Test seam (internal, not public API): sample an explicit PID list after taking the same identity
    /// snapshot that the production Job path takes before membership enumeration.
    let internal readMemberStatsForPids (job: nativeint) (pids: int list) : MemberStats list =
        match systemProcessIdentitySnapshot () with
        | Some identities -> readMemberStatsForPidsWithIdentities job identities pids
        | None -> []

    /// Sample every current Job member with a safe query-only handle. A process that exits between Job
    /// enumeration and handle verification is omitted; a member whose individual metrics are denied is
    /// retained with `None` for those metrics only when its pre-sampling identity still matches.
    let readMemberStats (job: nativeint) : Result<MemberStats list, ProcessError> =
        match systemProcessIdentitySnapshot () with
        | None ->
            // Without a generation ledger, a numeric Job PID cannot safely be attributed. Returning an
            // empty best-effort result is safer than exposing a possible recycled process.
            Ok []
        | Some identities ->
            match membersWindows job with
            | Error error -> Error error
            | Ok pids -> Ok(readMemberStatsForPidsWithIdentities job identities pids)

    // Open ONE Job member for the suspend/resume walk: PROCESS_SUSPEND_RESUME to deliver the operation,
    // PROCESS_QUERY_LIMITED_INFORMATION so the SAME handle can answer `IsProcessInJob` — the least
    // privilege this walk needs, and no second open. The Win32 error is captured immediately after the
    // call, so the classification below works off a returned value rather than thread-global state.
    let private openControlHandle (pid: int) : Result<nativeint, int> =
        match openControlHandleForTests with
        | Some opener -> opener pid
        | None ->
            let handle =
                OpenProcess(PROCESS_SUSPEND_RESUME ||| PROCESS_QUERY_LIMITED_INFORMATION, false, uint32 pid)

            if handle = IntPtr.Zero then
                Error(Marshal.GetLastWin32Error())
            else
                Ok handle

    // How many failed pids the aggregated error names before it summarises the rest. A job can hold
    // thousands of processes; this keeps the message actionable instead of turning it into a pid dump.
    [<Literal>]
    let private maxReportedFailedMembers = 8

    // Second opinion on a member the walk could neither open nor verify — the deliberately conservative
    // side of the one classification that cannot be made from the failed call alone (an open or a
    // membership query can be refused for reasons that do NOT prove the process is gone, e.g.
    // ERROR_ACCESS_DENIED). Only the JOB is authoritative about who its members are, so ask it again:
    //   * the pid has left the job — benign, this was the exit/recycle race the fail-safe skip exists for;
    //   * the job still lists it — a member we genuinely failed to touch, and the caller must hear it;
    //   * the re-query itself failed — the doubt cannot be resolved, so report rather than fabricate a
    //     success, matching how `membersWindows` already treats its own query failures.
    // Only reached on a failure, so the extra query costs nothing on the healthy path.
    let private unresolvedMemberFailure (job: nativeint) (pid: int) : string option =
        match membersWindows job with
        | Ok pids when pids |> List.contains pid -> Some "the job still lists it as a member"
        | Ok _ -> None
        | Error error -> Some $"its job membership could not be re-checked ({error.Message})"

    // Suspend or resume ONE member, classifying every way the attempt can end. `None` = nothing to report
    // (the member got the operation, or is provably not ours, or is provably gone); `Some detail` = a real
    // member did NOT get it, and `detail` says why.
    let private controlOneMember
        (job: nativeint)
        (operation: string)
        (action: nativeint -> int)
        (pid: int)
        : string option =
        match openControlHandle pid with
        | Error ERROR_INVALID_PARAMETER ->
            // Proven benign: the pid does not exist, so this member exited between the snapshot and the
            // open (the ordinary exit race of a short-lived grandchild). Nothing left to freeze or thaw.
            None
        | Error errno ->
            unresolvedMemberFailure job pid
            |> Option.map (fun reason -> $"could not be opened ({Win32Exception(errno).Message}) and {reason}")
        | Ok handle ->
            try
                let mutable stillMember = false

                if not (IsProcessInJob(handle, job, &stillMember)) then
                    // Membership is unverifiable, so this handle must never be touched (an unverified
                    // process could be a stranger). Same ambiguity as a refused open — ask the job.
                    let errno = Marshal.GetLastWin32Error()

                    unresolvedMemberFailure job pid
                    |> Option.map (fun reason ->
                        $"its job membership could not be verified ({Win32Exception(errno).Message}) and {reason}")
                elif not stillMember then
                    // The opened process is NOT in this job: the member exited and its pid was reused by an
                    // unrelated process. Benign by definition — a stranger is not a member we failed to
                    // suspend, and diverting the operation onto it is exactly what this re-check prevents.
                    None
                else
                    // Confirmed live (the handle opened) and confirmed ours (the job says so), so the
                    // NTSTATUS is about a real member and cannot be discarded.
                    match action handle with
                    | STATUS_SUCCESS -> None
                    | STATUS_PROCESS_IS_TERMINATING ->
                        // It began exiting between the membership check and this call — the same benign
                        // race as a vanished pid, one window later.
                        None
                    | status -> Some $"the native {operation} failed (NTSTATUS 0x{status:X8})"
            finally
                CloseHandle handle |> ignore

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
    // `OpenProcess`. `controlOneMember` re-verifies with `IsProcessInJob` that the just-opened handle is
    // STILL a member of THIS job before invoking `action` (`NtSuspendProcess`/`NtResumeProcess`), so a
    // recycled pid can never divert a suspend/resume onto a foreign process; an uncertain result never
    // touches the process.
    //
    // Honest: uncertainty about WHOM to touch is not the same as success. Every member that was confirmed
    // ours and still failed is aggregated here and reported as one `ProcessError.Io`, so a caller can no
    // longer be told the whole tree is frozen while some of it kept running. Delivery is not rolled back
    // for the members that did succeed — a partial suspend is reported, not undone, because unfreezing
    // them would be a second guess at what the caller wants (and Windows suspend counts make it lossy).
    let private forEachMemberHandle
        (job: nativeint)
        (operation: string)
        (action: nativeint -> int)
        : Result<unit, ProcessError> =
        match membersWindows job with
        | Error error -> Error error
        | Ok pids ->
            let failures = ResizeArray<int * string>()

            for pid in pids do
                match controlOneMember job operation action pid with
                | Some detail -> failures.Add(pid, detail)
                | None -> ()

            if failures.Count = 0 then
                Ok()
            else
                let named =
                    failures
                    |> Seq.truncate maxReportedFailedMembers
                    |> Seq.map (fst >> string)
                    |> String.concat ", "

                let listed =
                    if failures.Count > maxReportedFailedMembers then
                        $"{named} and {failures.Count - maxReportedFailedMembers} more"
                    else
                        named

                let firstPid, firstDetail = failures[0]

                Error(
                    ProcessError.Io
                        $"could not {operation} {failures.Count} of {List.length pids} job members (pids {listed}); first failure: pid {firstPid} — {firstDetail}"
                )

    let suspendWindows (job: nativeint) : Result<unit, ProcessError> =
        forEachMemberHandle job "suspend" suspendProcessHook

    let resumeWindows (job: nativeint) : Result<unit, ProcessError> =
        forEachMemberHandle job "resume" resumeProcessHook

    /// Adopt an already-running external process into `job` via `AssignProcessToJobObject`. Opens our own
    /// least-privilege handle to `pid` (PROCESS_SET_QUOTA + PROCESS_TERMINATE — the two rights the assign
    /// requires — plus PROCESS_QUERY_LIMITED_INFORMATION for the failure disambiguation), assigns it, then
    /// closes that handle: Job membership persists independently of the handle we assigned through, so the
    /// process stays contained (and visible to `membersWindows`, killed by `KILL_ON_JOB_CLOSE` at teardown)
    /// without us holding or tracking anything — it is not our child and needs no reap ledger entry.
    ///
    /// Every failure is a typed refusal, never a fabricated success:
    ///  * `OpenProcess` fails — ERROR_INVALID_PARAMETER (the pid does not exist: a lost adopt-vs-exit race)
    ///    or ERROR_ACCESS_DENIED (no rights to the foreign process) — both `ProcessError.Adopt`.
    ///  * `AssignProcessToJobObject` fails — if `IsProcessInJob` then reports the process is already in a
    ///    job, that is the "already in an incompatible Job (nested jobs not permitted here)" case; else it
    ///    is a generic assign failure (e.g. the target exited between open and assign). Either way
    ///    `ProcessError.Adopt` with the specific detail.
    let adoptIntoJob (job: nativeint) (pid: int) : Result<unit, ProcessError> =
        let opened =
            match adoptOpenProcessForTests with
            | Some hook -> hook pid
            | None ->
                let handle =
                    OpenProcess(
                        PROCESS_SET_QUOTA ||| PROCESS_TERMINATE ||| PROCESS_QUERY_LIMITED_INFORMATION,
                        false,
                        uint32 pid
                    )

                if handle = IntPtr.Zero then
                    Error(Marshal.GetLastWin32Error())
                else
                    Ok handle

        match opened with
        | Error errno ->
            let detail =
                if errno = ERROR_INVALID_PARAMETER then
                    "the process does not exist (it exited before it could be adopted, or its pid was never valid)"
                elif errno = ERROR_ACCESS_DENIED then
                    "access denied opening the process; the caller lacks the rights to adopt it into a Job"
                else
                    $"OpenProcess failed: {Win32Exception(errno).Message}"

            Error(ProcessError.Adopt(pid, detail))
        | Ok handle ->
            try
                if AssignProcessToJobObject(job, handle) then
                    Ok()
                else
                    let errno = Marshal.GetLastWin32Error()
                    let mutable alreadyInJob = false

                    let detail =
                        if IsProcessInJob(handle, IntPtr.Zero, &alreadyInJob) && alreadyInJob then
                            // Assigned to SOME job already (IsProcessInJob with a null job asks "in ANY job?").
                            // On a Windows configuration without nested-job support this is why the assign was
                            // refused (ERROR_ACCESS_DENIED); report it honestly rather than as "adopted".
                            "the process is already assigned to another Job that does not permit nesting on this Windows configuration"
                        elif errno = ERROR_ACCESS_DENIED then
                            "access denied assigning the process to the Job"
                        else
                            $"AssignProcessToJobObject failed: {Win32Exception(errno).Message}"

                    Error(ProcessError.Adopt(pid, detail))
            finally
                // The assign (on success) or its failure is complete; Job membership does not depend on
                // this handle, so drop it — we track nothing for an adopted, non-child process.
                CloseHandle handle |> ignore

    // Job-Object accounting for `stats`: cumulative CPU + active count (basic accounting) and peak
    // committed memory (extended limit info).
    [<Literal>]
    let private JobObjectBasicAndIoAccountingInformation = 8

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

    [<StructLayout(LayoutKind.Sequential)>]
    type private JOBOBJECT_BASIC_AND_IO_ACCOUNTING_INFORMATION =
        struct
            val mutable BasicInfo: JOBOBJECT_BASIC_ACCOUNTING_INFORMATION
            val mutable IoInfo: IO_COUNTERS
        end

    let private queryWindowsJobUserTime (job: nativeint) : int64 option =
        let size = Marshal.SizeOf<JOBOBJECT_BASIC_AND_IO_ACCOUNTING_INFORMATION>()
        let buffer = Marshal.AllocHGlobal size

        try
            let mutable returnLength = 0u

            if
                QueryInformationJobObject(
                    job,
                    JobObjectBasicAndIoAccountingInformation,
                    buffer,
                    uint32 size,
                    &returnLength
                )
            then
                let accounting =
                    Marshal.PtrToStructure<JOBOBJECT_BASIC_AND_IO_ACCOUNTING_INFORMATION> buffer

                Some accounting.BasicInfo.TotalUserTime
            else
                None
        finally
            Marshal.FreeHGlobal buffer

    /// Snapshot a Job's accounting: active processes, total CPU, peak committed memory, and cumulative
    /// read/write I/O counters. `None` if either query fails (e.g. the job handle was closed). CPU is
    /// user + kernel (100ns units, the same as a `TimeSpan` tick).
    let jobStatsWindows (job: nativeint) : (int * TimeSpan * int64 * ProcessIoCounters) option =
        let accSize = Marshal.SizeOf<JOBOBJECT_BASIC_AND_IO_ACCOUNTING_INFORMATION>()
        let extSize = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()
        let accBuffer = Marshal.AllocHGlobal accSize
        let extBuffer = Marshal.AllocHGlobal extSize

        try
            let mutable returnLength = 0u

            let okAcc =
                QueryInformationJobObject(
                    job,
                    JobObjectBasicAndIoAccountingInformation,
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
                let acc =
                    Marshal.PtrToStructure<JOBOBJECT_BASIC_AND_IO_ACCOUNTING_INFORMATION> accBuffer

                let ext = Marshal.PtrToStructure<JOBOBJECT_EXTENDED_LIMIT_INFORMATION> extBuffer

                let cpu =
                    TimeSpan.FromTicks(acc.BasicInfo.TotalUserTime + acc.BasicInfo.TotalKernelTime)

                let count (value: uint64) =
                    if value > uint64 Int64.MaxValue then
                        Int64.MaxValue
                    else
                        int64 value

                let io =
                    { ReadBytes = count acc.IoInfo.ReadTransferCount
                      WriteBytes = count acc.IoInfo.WriteTransferCount
                      ReadOperations = count acc.IoInfo.ReadOperationCount
                      WriteOperations = count acc.IoInfo.WriteOperationCount }

                Some(int acc.BasicInfo.ActiveProcesses, cpu, int64 ext.PeakJobMemoryUsed, io)
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
        | Some(active, _, _, _) -> active > 0
        | None -> true

    /// Hard-kill the whole contained tree: `TerminateJobObject` kills every process in the Job atomically.
    /// Returns what the OS actually did, never a fabricated success — a refused terminate on a Job that is
    /// still running processes is the one case a caller most needs to hear about, because "the tree is
    /// dead" is the entire promise of this call.
    ///
    /// A refusal is classified through the Job handle itself rather than through the Win32 error number
    /// (the same rule `terminateWindowsProcess` follows for a process handle): if the Job is provably
    /// EMPTY — its accounting reports no active process — there was nothing left to kill and the refusal is
    /// an idempotent no-op success, exactly like re-killing an already-reaped tree. If it still holds live
    /// members, or its accounting cannot be read at all (`jobTreeAliveWindows` fails SAFE to "alive", so an
    /// unreadable Job is never silently declared dead), the failure is reported as `ProcessError.Io`.
    ///
    /// The last-error read happens BEFORE the liveness probe, whose own P/Invokes would overwrite it.
    ///
    /// `KILL_ON_JOB_CLOSE` remains the backstop for the tree even when this returns `Error` — but it fires
    /// only when the last handle closes, so it must never be passed off as a completed kill NOW; that is
    /// precisely the difference this `Result` exists to express.
    let terminateWindowsJob (job: nativeint) : Result<unit, ProcessError> =
        if terminateJobObjectHook job 1u then
            Ok()
        else
            let message = Win32Exception(Marshal.GetLastWin32Error()).Message

            if jobTreeAliveWindows job then
                Error(ProcessError.Io $"failed to terminate the job object (its tree is still live): {message}")
            else
                Ok()

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
    // Targeted strictly by pid via `GetWindowThreadProcessId` — and re-confirmed by a SECOND owner query
    // taken immediately before each post (`postCloseIfStillOwnedBy`) — so, unlike a console CTRL event, it
    // can never reach a window outside the Job (no `CREATE_NEW_PROCESS_GROUP` requirement, no risk of
    // hitting the caller's own console group). That is why it is an UNCONDITIONAL addition to the soft
    // phase for every child, not a new opt-in builder: a child with no top-level window is simply a no-op,
    // never a regression. Honest and best-effort: a window may prompt/veto the close (WM_CLOSE is a
    // request), and the unconditional `TerminateJobObject` after the grace window remains the deterministic
    // guarantee.

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

    // Who owns `hWnd` at this instant. `0` is the documented "the owner could not be determined" answer —
    // also what a window handle that no longer names a live window reports — and is never a real pid, so
    // every caller below treats it as "not ours" rather than as a match.
    let private windowOwnerPid (hWnd: nativeint) : uint32 =
        let mutable owningPid = 0u
        GetWindowThreadProcessId(hWnd, &owningPid) |> ignore
        owningPid

    /// Test seam: the window → owning-pid query (`GetWindowThreadProcessId`). Both WM_CLOSE paths below ask
    /// it AGAIN immediately before every post, so a test can model an HWND that was destroyed and whose
    /// handle value was recycled onto a FOREIGN process's window in exactly that gap — a race that cannot
    /// be provoked against the real OS on demand. Production always runs the real entry point; only the
    /// (sequential) tests reassign it, and restore it in a `finally`.
    let mutable windowOwnerPidHook: nativeint -> uint32 = windowOwnerPid

    // ONE `EnumWindows` pass over the caller's desktop, as `(window, owning pid)` pairs. Windows are
    // collected here and acted on afterwards, so posting can never perturb an enumeration in flight.
    let private enumerateTopLevelWindows () : (nativeint * uint32) list =
        let collected = ResizeArray<nativeint * uint32>()

        let collect =
            EnumWindowsProc(fun hWnd _ ->
                collected.Add((hWnd, windowOwnerPidHook hWnd))
                true) // keep enumerating every remaining top-level window

        // EnumWindows walks the desktop synchronously, so `collect` is alive for the whole call;
        // `GC.KeepAlive` pins it against an over-eager collection, and its `bool` result is ignored
        // (FALSE here only signals an early stop / empty desktop — neither an error for this pass).
        EnumWindows(collect, IntPtr.Zero) |> ignore
        GC.KeepAlive collect
        List.ofSeq collected

    /// Test seam: the single desktop enumeration pass, so a regression test can hand the WM_CLOSE paths
    /// synthetic `(window, owning pid)` pairs instead of whatever windows the test host's desktop happens
    /// to show. Production always runs the real enumeration; only the (sequential) tests reassign it, and
    /// restore it in a `finally`.
    let mutable enumerateTopLevelWindowsHook: unit -> (nativeint * uint32) list =
        enumerateTopLevelWindows

    /// Test seam: the `PostMessage(WM_CLOSE)` delivery itself, so a regression test can assert exactly
    /// WHICH windows were posted to without standing up a real message pump. Production always runs the
    /// real entry point; only the (sequential) tests reassign it, and restore it in a `finally`.
    let mutable postWindowCloseHook: nativeint -> bool =
        fun hWnd -> PostMessageW(hWnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero)

    // The stale-window guard EVERY post goes through. A window handle is only meaningful while its window
    // lives: an HWND collected by the enumeration pass above can have its window destroyed a moment later
    // and its numeric value handed straight back out to the next window created on the desktop — quite
    // possibly a window of an unrelated GUI application. Neither caller's existing check rules that out:
    // the Job path's `IsProcessInJob` re-check proves only that the OWNING PID is still a live member, and
    // the per-run path's open process handle pins only the PID NUMBER — neither says anything about who
    // owns THIS window now. So ask exactly that, as the last thing before posting: the post happens only if
    // the window's CURRENT owner is still the process the caller verified.
    //
    // Why this is the narrowest window the API allows. Win32 has no atomic "post to this window if it is
    // still owned by that process" primitive, so the owner query and the post cannot be fused; ordering the
    // query LAST leaves only the handful of instructions between the two calls, against the whole
    // enumeration pass plus a per-target `OpenProcess`/`IsProcessInJob` round-trip before this guard
    // existed. It is a comparison of process IDENTITY, not of a bare number, because both callers hold an
    // open handle to the expected owner across this call — Windows cannot recycle a pid while a handle to
    // that process object is open (the same invariant the `ctrlGroups` CTRL+BREAK path already relies on),
    // so "same pid" here means "same process". Closing the residual gap completely would need a mechanism
    // that does not exist; what remains requires a destroy + create + identical-handle-value reuse inside
    // those few instructions, and a post to an already-destroyed window fails
    // (`ERROR_INVALID_WINDOW_HANDLE`) rather than reaching anything, which is why only a post the OS
    // accepted is counted as a delivery.
    let private postCloseIfStillOwnedBy (expectedPid: uint32) (hWnd: nativeint) : bool =
        let currentOwner = windowOwnerPidHook hWnd

        if currentOwner <> 0u && currentOwner = expectedPid then
            // A REQUEST, not a guarantee: the window's own close handler may prompt or veto. That is why
            // the post-grace `TerminateJobObject` is the unconditional backstop.
            postWindowCloseHook hWnd
        else
            false

    /// Best-effort soft close for a Windows GUI tree: enumerate the caller's desktop top-level windows
    /// ONCE, keep those owned by a process currently in `job`, then `PostMessage(WM_CLOSE)` to each window
    /// still owned by that member at the moment of the post. Returns the number of windows a post was
    /// accepted for — `0` means the tree has no top-level window (a no-op, NOT an error), matching how
    /// `sendConsoleCtrlBreakWindows`/`membersWindows` honestly distinguish "nothing to signal" from "the
    /// request failed". NEVER throws: a failed member query, a failed enumeration (e.g. a session with no
    /// interactive desktop), or a failed post is just reported as fewer windows closed, never an exception
    /// that could derail the graceful-kill path that calls it. TWO identity gates stand between the
    /// enumeration and each post: the owning pid must still be a member of THIS job (`IsProcessInJob` on a
    /// freshly opened handle — a member that exited and whose PID was recycled can never close a foreign
    /// application's window), and the window must still be owned by that same, handle-pinned process
    /// (`postCloseIfStillOwnedBy` — a member window that was destroyed and whose HWND value was recycled
    /// onto a foreign window can never be closed either).
    let postCloseToJobWindows (job: nativeint) : int =
        try
            let memberPids =
                match membersWindows job with
                | Ok pids -> Set.ofList pids
                | Error _ -> Set.empty

            if Set.isEmpty memberPids then
                0
            else
                let targets =
                    enumerateTopLevelWindowsHook ()
                    // `owningPid = 0` is the documented "could not determine the owner" result — never a
                    // real pid, so it can't spuriously match a member and is skipped.
                    |> List.filter (fun (_, owningPid) -> owningPid <> 0u && Set.contains (int owningPid) memberPids)

                let mutable postedCount = 0

                for hWnd, owningPid in targets do
                    let handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, owningPid)

                    if handle <> IntPtr.Zero then
                        try
                            // Gate 1 — is the owner still one of ours? Necessary (it rejects a member that
                            // exited and had its pid recycled), but NOT sufficient: it says nothing about
                            // who owns `hWnd` now. Holding this handle open across gate 2 below is what
                            // keeps `owningPid` bound to this one process object for the rest of the loop.
                            if isProcessInJob handle job then
                                // Gate 2 — is that member still the owner of THIS window?
                                if postCloseIfStillOwnedBy owningPid hWnd then
                                    postedCount <- postedCount + 1
                        finally
                            CloseHandle handle |> ignore

                postedCount
        with _ ->
            // Best-effort by contract (see the section comment): enumeration/post failing on a host with
            // no usable desktop must never throw into the graceful-kill path — report "nothing closed" and
            // let the unconditional hard kill proceed.
            0

    /// Best-effort WM_CLOSE for one process whose caller keeps an open process handle while invoking
    /// this function. The open handle pins `pid`, so the number cannot be recycled onto a foreign process
    /// during enumeration; the WINDOW handles it names are not pinned that way, so each window's current
    /// owner is re-checked immediately before its own post (`postCloseIfStillOwnedBy`) — a window that was
    /// destroyed and whose HWND value was recycled onto a foreign application's window in that gap is
    /// skipped instead of being closed. Returns the number of matching top-level windows a post was
    /// accepted for.
    let postCloseToProcessWindows (pid: int) : int =
        try
            let expectedPid = uint32 pid

            let targets =
                enumerateTopLevelWindowsHook ()
                |> List.filter (fun (_, owningPid) -> owningPid <> 0u && owningPid = expectedPid)
                |> List.map fst

            let mutable postedCount = 0

            for hWnd in targets do
                if postCloseIfStillOwnedBy expectedPid hWnd then
                    postedCount <- postedCount + 1

            postedCount
        with _ ->
            // WM_CLOSE is a best-effort Windows analogue; a missing desktop must stay a typed
            // unsupported delivery at the caller rather than faulting the process-control path.
            0

    // Job-Object resource limits (the `limits` backend on Windows).
    [<Literal>]
    let private JOB_OBJECT_LIMIT_ACTIVE_PROCESS = 0x00000008u

    [<Literal>]
    let private JOB_OBJECT_LIMIT_JOB_TIME = 0x00000004u

    [<Literal>]
    let private JOB_OBJECT_LIMIT_PRESERVE_JOB_TIME = 0x00000040u

    [<Literal>]
    let private JOB_OBJECT_LIMIT_JOB_MEMORY = 0x00000200u

    // `JOBOBJECT_BASIC_LIMIT_INFORMATION.Affinity` is honoured only when this flag is set; without it the
    // field is ignored and the tree keeps the ambient affinity (which is why clearing the pin is just a
    // matter of NOT setting the flag — there is no separate "disable" call to make, and so no analogue of
    // the CPU-rate-control ERROR_INVALID_PARAMETER-on-already-disabled case below).
    [<Literal>]
    let private JOB_OBJECT_LIMIT_AFFINITY = 0x00000010u

    [<Literal>]
    let private JobObjectCpuRateControlInformation = 15

    [<Literal>]
    let private JOB_OBJECT_CPU_RATE_CONTROL_ENABLE = 0x1u

    [<Literal>]
    let private JOB_OBJECT_CPU_RATE_CONTROL_HARD_CAP = 0x4u

    [<Literal>]
    let private JOB_OBJECT_IO_RATE_CONTROL_ENABLE = 0x1u

    [<Literal>]
    let private ERROR_INVALID_FUNCTION = 1

    [<Literal>]
    let private ERROR_NOT_SUPPORTED = 50

    [<Literal>]
    let private ERROR_CALL_NOT_IMPLEMENTED = 120

    [<Literal>]
    let private ERROR_PROC_NOT_FOUND = 127

    // Fault injection for the late CPU-rate write, after the extended-limit block has already landed.
    // Production leaves this unset; the Windows rollback regression tests install and clear it in a
    // `finally` block so the native rollback path is exercised deterministically.
    let mutable cpuRateWriteErrorForTests: int option = None

    // One-shot fault injection for the Job I/O rate write. Production leaves this unset; Windows-gated
    // tests use it to force one late native failure while allowing the rollback write to reach the OS.
    let mutable ioRateWriteErrorForTests: int option = None

    // Successful native I/O writes observed by Windows-gated rollback tests. Production leaves capture
    // disabled, and the bounded buffer is protected because groups can update concurrently.
    let private ioRateWriteSuccessesGate = obj ()
    let private maxIoRateWriteSuccessesForTests = 32
    let mutable private ioRateWriteSuccessCaptureEnabledForTests = 0

    let mutable private ioRateWriteSuccessesStateForTests: (string * int64 * int64 * bool) list =
        []

    let enableIoRateWriteSuccessCaptureForTests () =
        lock ioRateWriteSuccessesGate (fun () ->
            ioRateWriteSuccessesStateForTests <- []
            Interlocked.Exchange(&ioRateWriteSuccessCaptureEnabledForTests, 1) |> ignore)

    let disableIoRateWriteSuccessCaptureForTests () =
        lock ioRateWriteSuccessesGate (fun () ->
            Interlocked.Exchange(&ioRateWriteSuccessCaptureEnabledForTests, 0) |> ignore
            ioRateWriteSuccessesStateForTests <- [])

    let ioRateWriteSuccessesForTests () =
        lock ioRateWriteSuccessesGate (fun () -> ioRateWriteSuccessesStateForTests)

    let private captureIoRateWriteSuccessForTests entry =
        if Interlocked.CompareExchange(&ioRateWriteSuccessCaptureEnabledForTests, 0, 0) = 1 then
            lock ioRateWriteSuccessesGate (fun () ->
                if Interlocked.CompareExchange(&ioRateWriteSuccessCaptureEnabledForTests, 0, 0) = 1 then
                    ioRateWriteSuccessesStateForTests <-
                        (entry :: ioRateWriteSuccessesStateForTests)
                        |> List.truncate maxIoRateWriteSuccessesForTests)

    [<StructLayout(LayoutKind.Sequential)>]
    type private JOBOBJECT_CPU_RATE_CONTROL_INFORMATION =
        struct
            val mutable ControlFlags: uint32
            // Union member: the hard-cap rate as 1/100ths of a percent of total system CPU (1..10000).
            val mutable CpuRate: uint32
        end

    [<StructLayout(LayoutKind.Sequential)>]
    type private JOBOBJECT_IO_RATE_CONTROL_INFORMATION =
        struct
            val mutable MaxIops: int64
            val mutable MaxBandwidth: int64
            val mutable ReservationIops: int64
            val mutable VolumeName: nativeint
            val mutable BaseIoSize: uint32
            val mutable ControlFlags: uint32
        end

    [<DllImport("kernel32.dll", SetLastError = true, EntryPoint = "SetIoRateControlInformationJobObject")>]
    extern uint32 private SetIoRateControlInformationJobObject(nativeint hJob, nativeint ioRateControlInfo)

    // Job Object UI restrictions (`ProcessGroupOptions.WithUiRestrictions`): one flags word denying the
    // contained tree clipboard/desktop/display/exit-Windows access. The `WindowsUiRestrictions` values are
    // the Win32 `JOB_OBJECT_UILIMIT_*` bits verbatim, so the mapping is a plain widening conversion.
    [<Literal>]
    let private JobObjectBasicUIRestrictions = 4

    [<StructLayout(LayoutKind.Sequential)>]
    type private JOBOBJECT_BASIC_UI_RESTRICTIONS =
        struct
            val mutable UIRestrictionsClass: uint32
        end

    /// Capture the Job's current extended-limit block (the memory + active-process caps and their flags),
    /// so a live limit update can roll those caps back to exactly this state should a LATER native write
    /// (the CPU rate cap) fail after this block was already replaced. `None` if the query fails — a
    /// guaranteed rollback is then impossible, which the caller surfaces honestly rather than silently.
    /// Only the input fields (BasicLimitInformation + JobMemoryLimit) matter when the struct is written
    /// back; the accounting/peak output fields the query also fills are ignored by `SetInformationJobObject`.
    let private queryExtendedLimit (job: nativeint) : JOBOBJECT_EXTENDED_LIMIT_INFORMATION option =
        let size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()
        let buffer = Marshal.AllocHGlobal size

        try
            let mutable returnLength = 0u

            if
                QueryInformationJobObject(job, JobObjectExtendedLimitInformation, buffer, uint32 size, &returnLength)
            then
                Some(Marshal.PtrToStructure<JOBOBJECT_EXTENDED_LIMIT_INFORMATION> buffer)
            else
                None
        finally
            Marshal.FreeHGlobal buffer

    /// The Job's current UI-restriction flags (`JOBOBJECT_BASIC_UI_RESTRICTIONS`), or `None` if the query
    /// fails. Serves two purposes: the limit-apply path uses it to skip a no-op rewrite and to roll the
    /// restrictions back when a later write fails (the same contract `queryExtendedLimit` gives the
    /// memory/active-process block), and it is the read-back a test asserts the applied set against.
    let queryWindowsUiRestrictions (job: nativeint) : uint32 option =
        let size = Marshal.SizeOf<JOBOBJECT_BASIC_UI_RESTRICTIONS>()
        let buffer = Marshal.AllocHGlobal size

        try
            let mutable returnLength = 0u

            if QueryInformationJobObject(job, JobObjectBasicUIRestrictions, buffer, uint32 size, &returnLength) then
                Some (Marshal.PtrToStructure<JOBOBJECT_BASIC_UI_RESTRICTIONS> buffer).UIRestrictionsClass
            else
                None
        finally
            Marshal.FreeHGlobal buffer

    /// The Job's current CPU-affinity mask, or `None` when it carries no affinity limit at all (or the
    /// query failed). The read-back a test asserts an applied pin against, mirroring
    /// `queryWindowsUiRestrictions` — the mask field is only meaningful while `JOB_OBJECT_LIMIT_AFFINITY`
    /// is set, so an unpinned Job reports `None` rather than whatever stale bits the field happens to
    /// hold.
    let queryWindowsJobAffinity (job: nativeint) : unativeint option =
        match queryExtendedLimit job with
        | Some info when info.BasicLimitInformation.LimitFlags &&& JOB_OBJECT_LIMIT_AFFINITY <> 0u ->
            Some info.BasicLimitInformation.Affinity
        | _ -> None

    /// The Job Object affinity mask for a requested core set, or an honest message naming the cores that
    /// have no representation in it. `JOBOBJECT_BASIC_LIMIT_INFORMATION.Affinity` is a single
    /// pointer-sized bitmask scoped to ONE processor group — 64 bits on x64, 32 on x86 — so a core index
    /// at or beyond that width cannot be expressed at all. Refusing it here (before any native write)
    /// keeps the shift from wrapping the index around onto a different, wrong core, which is exactly the
    /// silent downgrade this library refuses to make: the caller gets a typed `ResourceLimit` naming the
    /// cores instead.
    let windowsAffinityMask (cores: int list) : Result<unativeint, string> =
        let bits = IntPtr.Size * 8

        match cores |> List.filter (fun core -> core >= bits) with
        | [] -> cores |> List.fold (fun mask core -> mask ||| (1un <<< core)) 0un |> Ok
        | unrepresentable ->
            let listed = unrepresentable |> List.map string |> String.concat ", "

            Error
                $"CPU core(s) {listed} cannot be pinned through a Windows Job Object: its affinity mask is one {bits}-bit word covering a single processor group, so only cores 0-{bits - 1} can be named"

    /// The extended-limit block a limit set asks for — the memory and active-process caps, the CPU
    /// affinity pin, and the preserved `KILL_ON_JOB_CLOSE` — or an honest message when the requested pin
    /// has no representation in a Job affinity mask. Resolved as a pure value, with no native call, so
    /// `applyWindowsJobLimits` can refuse an impossible set while the Job is still wholly untouched.
    ///
    /// A dimension left `None` simply does not set its flag, which IS the replace semantics: the block is
    /// written whole, so an unset flag means that cap is lifted. Affinity is no exception — dropping the
    /// pin needs no separate "disable" call (contrast the CPU rate control, which does).
    let private extendedLimitBlockFor
        (preserveJobTime: bool)
        (limits: ResourceLimits)
        : Result<JOBOBJECT_EXTENDED_LIMIT_INFORMATION, string> =
        let affinity =
            match limits.CpuAffinityCores with
            | Some cores -> windowsAffinityMask cores |> Result.map Some
            | None -> Ok Option.None

        match affinity with
        | Error message -> Error message
        | Ok affinityMask ->
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

            match limits.CpuTimeMax with
            | Some _ when preserveJobTime -> flags <- flags ||| JOB_OBJECT_LIMIT_PRESERVE_JOB_TIME
            | Some duration ->
                flags <- flags ||| JOB_OBJECT_LIMIT_JOB_TIME
                info.BasicLimitInformation.PerJobUserTimeLimit <- duration.Ticks
            | None -> ()

            match affinityMask with
            | Some mask ->
                flags <- flags ||| JOB_OBJECT_LIMIT_AFFINITY
                info.BasicLimitInformation.Affinity <- mask
            | None -> ()

            info.BasicLimitInformation.LimitFlags <- flags
            Ok info

    let private ioRateUnsupportedPrefix =
        "Windows Job Object I/O rate control is unavailable"

    /// Exposed to the backend only for classifying the documented unsupported native API outcome as
    /// `ProcessError.Unsupported`; all other SetIoRate failures remain `ProcessError.ResourceLimit`.
    let isIoRateControlUnsupported (message: string) =
        message.StartsWith(ioRateUnsupportedPrefix, StringComparison.Ordinal)

    let private ioRateErrorMessage (code: int) =
        let nativeMessage = Win32Exception(code).Message

        if
            code = ERROR_INVALID_FUNCTION
            || code = ERROR_NOT_SUPPORTED
            || code = ERROR_CALL_NOT_IMPLEMENTED
            || code = ERROR_PROC_NOT_FOUND
        then
            $"{ioRateUnsupportedPrefix} (Win32 error {code}: {nativeMessage})"
        else
            $"SetIoRateControlInformationJobObject failed (Win32 error {code}: {nativeMessage})"

    let private writeIoRateControl
        (job: nativeint)
        (target: string)
        (maxBandwidth: int64)
        (maxIops: int64)
        (enabled: bool)
        : Result<unit, string> =
        let mutable info = JOBOBJECT_IO_RATE_CONTROL_INFORMATION()
        info.MaxBandwidth <- maxBandwidth
        info.MaxIops <- maxIops
        info.ReservationIops <- 0L
        info.BaseIoSize <- 0u
        info.ControlFlags <- if enabled then JOB_OBJECT_IO_RATE_CONTROL_ENABLE else 0u

        let volumeBuffer = Marshal.StringToHGlobalUni target
        let infoSize = Marshal.SizeOf<JOBOBJECT_IO_RATE_CONTROL_INFORMATION>()
        let infoBuffer = Marshal.AllocHGlobal infoSize

        try
            info.VolumeName <- volumeBuffer
            Marshal.StructureToPtr<JOBOBJECT_IO_RATE_CONTROL_INFORMATION>(info, infoBuffer, false)

            let result =
                match Interlocked.Exchange(&ioRateWriteErrorForTests, None) with
                | Some code -> Error code
                | None ->
                    try
                        if SetIoRateControlInformationJobObject(job, infoBuffer) <> 0u then
                            Ok()
                        else
                            Error(Marshal.GetLastWin32Error())
                    with
                    | :? EntryPointNotFoundException -> Error ERROR_PROC_NOT_FOUND
                    | :? DllNotFoundException -> Error ERROR_PROC_NOT_FOUND

            match result with
            | Ok() ->
                captureIoRateWriteSuccessForTests (target, maxBandwidth, maxIops, enabled)

                Ok()
            | Error code -> Error(ioRateErrorMessage code)
        finally
            Marshal.FreeHGlobal infoBuffer
            Marshal.FreeHGlobal volumeBuffer

    let private ioRateValues (ioMax: IoMax) : Result<int64 * int64, string> =
        if ioMax.ReadBytesPerSecond <> ioMax.WriteBytesPerSecond then
            Error "Windows Job Object I/O rate control is per-volume and aggregate; read/write byte ceilings must match"
        elif ioMax.ReadOperationsPerSecond <> ioMax.WriteOperationsPerSecond then
            Error
                "Windows Job Object I/O rate control is per-volume and aggregate; read/write operation ceilings must match"
        else
            Ok(Option.defaultValue 0L ioMax.ReadBytesPerSecond, Option.defaultValue 0L ioMax.ReadOperationsPerSecond)

    let private writeIoMax (job: nativeint) (ioMax: IoMax) : Result<unit, string> =
        ioRateValues ioMax
        |> Result.bind (fun (bandwidth, iops) -> writeIoRateControl job ioMax.Target bandwidth iops true)

    let private disableIoMax (job: nativeint) (ioMax: IoMax) =
        writeIoRateControl job ioMax.Target 0L 0L false

    /// Replace the one recorded Job I/O policy. Disable is only sent when `previous` is Some, so an
    /// already-unset Job never receives the native invalid-parameter disable request (K-054).
    let private updateIoMax (job: nativeint) (previous: IoMax option) (requested: IoMax option) : Result<unit, string> =
        let restore prior error =
            match prior with
            | None -> Error error
            | Some old ->
                match writeIoMax job old with
                | Ok() -> Error error
                | Error restoreError ->
                    Error(
                        $"{error}; the previous Windows Job I/O rate could not be restored ({restoreError}), so it may be partially applied"
                    )

        match previous, requested with
        | None, None -> Ok()
        | None, Some next -> writeIoMax job next
        | Some old, None -> disableIoMax job old
        | Some old, Some next when old.Target = next.Target ->
            match writeIoMax job next with
            | Ok() -> Ok()
            | Error error -> restore (Some old) error
        | Some old, Some next ->
            match disableIoMax job old with
            | Error error -> Error error
            | Ok() ->
                match writeIoMax job next with
                | Ok() -> Ok()
                | Error error -> restore (Some old) error

    /// Apply resource limits to a Job: a memory cap (`JobMemoryLimit`), an active-process cap, a
    /// CPU hard cap (a fraction of *total* system CPU, so per-core quota is approximate), the CPU-affinity
    /// pin (`Affinity` + `JOB_OBJECT_LIMIT_AFFINITY`), and the UI restrictions
    /// (clipboard/desktop/display/exit-Windows). Preserves `KILL_ON_JOB_CLOSE`.
    /// Returns an error message on failure.
    ///
    /// This cleanly REPLACES the caps in force, so it serves both `ProcessGroup.Create` (a fresh Job)
    /// and `ProcessGroup.UpdateLimits` (a live Job): `SetInformationJobObject` overwrites the whole
    /// extended-limit block, so a dimension left `None` (its flag not set) is written back as unbounded.
    /// The CPU rate control is enabled with the hard cap when a quota is set; when it is `None` the cap
    /// is explicitly DISABLED (`ControlFlags = 0`) so an update that drops the CPU quota removes a
    /// previously-applied cap rather than silently leaving it in force — and disabling on a Job that had
    /// no CPU cap (a fresh Job, or a `None`→`None` update) reports `ERROR_INVALID_PARAMETER`, which is
    /// exactly the desired "no CPU cap" end state and so is treated as success.
    ///
    /// The caps land in separate native writes — the UI restrictions, the extended-limit block (memory +
    /// active-process + affinity), then the CPU rate block — so a later one could fail after an earlier one
    /// already applied. To keep the honest `UpdateLimits` contract (a failed apply leaves the live Job on
    /// the PREVIOUS set), each prior block is captured up front and best-effort restored if a later write
    /// fails, so an `Error` return means the Job is back on the previous set, never a silent mix
    /// `Options.Limits` would misreport (T-207). Only if a restore itself fails (or a prior couldn't be
    /// captured) is the state indeterminate, and the error says so distinctly. The affinity pin needs no
    /// rollback machinery of its own: it rides in the extended-limit block, which the kernel applies whole
    /// or not at all, so it is already covered by that block's captured prior.
    ///
    /// The UI restrictions are written FIRST, and only when they actually differ from what the Job
    /// already carries: a failure there has then changed nothing at all (the same "nothing to roll back"
    /// position as a failed first block), and the overwhelmingly common case — a limit set with no UI
    /// restrictions on a Job that has none — issues no extra native call. Ahead of even that, the whole
    /// extended-limit block is resolved as a pure value (`extendedLimitBlockFor`), so a pin no affinity
    /// mask can express is refused with the Job untouched rather than half-updated.
    let private applyWindowsJobLimitsCore
        (preserveJobTime: bool)
        (job: nativeint)
        (limits: ResourceLimits)
        : Result<unit, string> =
        // (Re)write the Job's CPU rate control block. `controlFlags = 0` disables CPU rate control (the
        // replace-semantics "no CPU cap" state); the enable+hard-cap flags with a rate arm the cap. The
        // raw Win32 errno is returned on failure so the caller can classify it (see the `None` branch).
        let writeCpuRate (controlFlags: uint32) (rate: uint32) : Result<unit, int> =
            match cpuRateWriteErrorForTests with
            | Some errno -> Error errno
            | None ->
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

        // Serialize an extended-limit block and hand it to `SetInformationJobObject`. Factored out so the
        // SAME primitive both applies the NEW block and restores a captured PRIOR block on a rollback.
        let writeExtendedLimit (info: JOBOBJECT_EXTENDED_LIMIT_INFORMATION) : Result<unit, string> =
            let size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()
            let buffer = Marshal.AllocHGlobal size

            try
                Marshal.StructureToPtr<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>(info, buffer, false)

                if SetInformationJobObject(job, JobObjectExtendedLimitInformation, buffer, uint32 size) then
                    Ok()
                else
                    Error(Win32Exception(Marshal.GetLastWin32Error()).Message)
            finally
                Marshal.FreeHGlobal buffer

        // (Re)write the Job's UI-restriction flags — a single flags word with the same replace semantics
        // as the blocks above: `0` means "no UI restrictions", so a set dropped from an update is
        // genuinely lifted rather than left in force.
        let writeUiRestrictions (restrictions: uint32) : Result<unit, string> =
            let mutable uiInfo = JOBOBJECT_BASIC_UI_RESTRICTIONS()
            uiInfo.UIRestrictionsClass <- restrictions
            let uiSize = Marshal.SizeOf<JOBOBJECT_BASIC_UI_RESTRICTIONS>()
            let uiBuffer = Marshal.AllocHGlobal uiSize

            try
                Marshal.StructureToPtr<JOBOBJECT_BASIC_UI_RESTRICTIONS>(uiInfo, uiBuffer, false)

                if SetInformationJobObject(job, JobObjectBasicUIRestrictions, uiBuffer, uint32 uiSize) then
                    Ok()
                else
                    // Captured inline, before the `finally` runs a P/Invoke that could reset it.
                    Error(Win32Exception(Marshal.GetLastWin32Error()).Message)
            finally
                Marshal.FreeHGlobal uiBuffer

        // The Job's current caps, captured BEFORE the replacement so a later failure that lands after an
        // earlier block was already overwritten can put those caps back exactly as they were.
        let priorExt = queryExtendedLimit job
        let priorUi = queryWindowsUiRestrictions job
        let requestedUi = uint32 (int limits.UiRestrictions)

        // Best-effort restore of the Job's prior UI restrictions after a LATER write failed. `None` when
        // there is nothing to undo or the undo succeeded; `Some reason` when the Job may still carry the
        // half-applied set, which the caller's error must then say out loud.
        let restoreUiRestrictions (uiChanged: bool) : string option =
            if not uiChanged then
                None
            else
                match priorUi with
                | Some prior ->
                    match writeUiRestrictions prior with
                    | Ok() -> None
                    | Error message -> Some message
                | None -> Some "the Job's prior UI restrictions could not be captured"

        // Append a failed UI rollback to an error message, so a caller is never told the Job was cleanly
        // put back on its previous set when one half of it was not.
        let withUiNote (message: string) (uiNote: string option) =
            match uiNote with
            | None -> message
            | Some note ->
                $"{message}; the Job's previous UI restrictions could not be restored either ({note}), so they may still be in force"

        // Win32 rejects an affinity mask that is not a subset of this process's own affinity with a bare
        // ERROR_INVALID_PARAMETER — "the parameter is incorrect", which names no core and no reason. When
        // the block being written carries a pin, say what was asked for, so a request for a core this host
        // does not have (or will not give this process) is actionable rather than a riddle.
        let withAffinityNote (message: string) =
            match limits.CpuAffinityCores with
            | Some cores ->
                let listed = cores |> List.map string |> String.concat ", "

                $"{message} (the requested CPU affinity pins the tree to core(s) {listed}; a Job's affinity mask must be a subset of this process's own, so every core named must exist on this host and be available to it)"
            | None -> message

        // Restore a captured extended-limit block without granting the Job fresh CPU time. Windows
        // interprets a JOB_TIME write as a *remaining* budget and internally adds TotalUserTime, even
        // though querying the same field returns the absolute deadline. When an unrelated update used
        // PRESERVE, keep that deadline in place with another PRESERVE write. When the failed update had
        // changed or removed CpuTimeMax, rebuild the prior absolute deadline from its remaining budget;
        // accounting can advance between the query and write, so this path has unavoidable sub-tick race
        // slop, and a fully-consumed prior budget is clamped to the smallest representable positive value.
        let restoreExtendedLimit (prior: JOBOBJECT_EXTENDED_LIMIT_INFORMATION) =
            let priorFlags = prior.BasicLimitInformation.LimitFlags

            if priorFlags &&& JOB_OBJECT_LIMIT_JOB_TIME = 0u then
                writeExtendedLimit prior
            else
                let mutable restored = prior

                if preserveJobTime then
                    restored.BasicLimitInformation.LimitFlags <-
                        (priorFlags &&& ~~~JOB_OBJECT_LIMIT_JOB_TIME)
                        ||| JOB_OBJECT_LIMIT_PRESERVE_JOB_TIME

                    restored.BasicLimitInformation.PerJobUserTimeLimit <- 0L
                    writeExtendedLimit restored
                else
                    match queryWindowsJobUserTime job with
                    | Some used ->
                        restored.BasicLimitInformation.PerJobUserTimeLimit <-
                            max 1L (prior.BasicLimitInformation.PerJobUserTimeLimit - used)

                        writeExtendedLimit restored
                    | None -> Error "could not query the Job's consumed user time for CPU-time rollback"

        match extendedLimitBlockFor preserveJobTime limits with
        | Error message ->
            // A pin no Job affinity mask can express. Refused before a single native write, so the Job
            // still carries exactly the caps it had.
            Error message
        | Ok info ->

            // Skipped entirely when the Job already carries exactly the requested set (including the common
            // "none requested, none in force"); written unconditionally when the prior could not be read,
            // since nothing then proves the Job is already right.
            let uiWrite =
                if priorUi = Some requestedUi then
                    Ok false
                else
                    writeUiRestrictions requestedUi |> Result.map (fun () -> true)

            match uiWrite with
            | Error message ->
                // Nothing else has been touched yet, so the previous set is still wholly in force.
                Error $"failed to apply the Job UI restrictions: {message}"
            | Ok uiChanged ->

                match writeExtendedLimit info with
                | Error message ->
                    // `SetInformationJobObject` applies the whole block or none, so a failure here changed
                    // nothing in the memory/active-process/affinity caps — but the UI restrictions written
                    // just above did change, so put those back before reporting.
                    Error(withUiNote (withAffinityNote message) (restoreUiRestrictions uiChanged))
                | Ok() ->
                    let cpuResult =
                        match limits.CpuQuota with
                        | Some cores ->
                            let fraction = min 1.0 (cores / float Environment.ProcessorCount)
                            let rate = uint32 (max 1.0 (Math.Round(fraction * 10000.0)))

                            writeCpuRate
                                (JOB_OBJECT_CPU_RATE_CONTROL_ENABLE ||| JOB_OBJECT_CPU_RATE_CONTROL_HARD_CAP)
                                rate
                            |> Result.mapError (fun errno -> Win32Exception(errno).Message)
                        | None ->
                            // Replace semantics: no CPU quota now, so disable any rate cap a prior apply set.
                            // Disabling on a Job that has none enabled is rejected with ERROR_INVALID_PARAMETER —
                            // the "no CPU cap" state already holds, so treat that as success; surface anything else.
                            match writeCpuRate 0u 0u with
                            | Ok() -> Ok()
                            | Error errno when errno = ERROR_INVALID_PARAMETER -> Ok()
                            | Error errno -> Error(Win32Exception(errno).Message)

                    match cpuResult with
                    | Ok() -> Ok()
                    | Error cpuMessage ->
                        // The memory/active-process/affinity block already applied but the CPU-rate cap did
                        // not. Roll that block (and the UI restrictions) back to the captured prior so the
                        // live Job and the Options snapshot stay together on the previous set (T-207); if a
                        // prior couldn't be captured, or restoring it also fails, the state is indeterminate
                        // — say so distinctly so the caller never trusts Options here.
                        let uiNote = restoreUiRestrictions uiChanged

                        match priorExt with
                        | Some prior ->
                            match restoreExtendedLimit prior with
                            | Ok() -> Error(withUiNote cpuMessage uiNote)
                            | Error restoreMessage ->
                                Error(
                                    withUiNote
                                        $"failed to apply the Job CPU rate cap ({cpuMessage}) and could not roll the memory/active-process/affinity caps back to the previous set ({restoreMessage}); the Job's limits may be partially applied"
                                        uiNote
                                )
                        | None ->
                            Error(
                                withUiNote
                                    $"failed to apply the Job CPU rate cap ({cpuMessage}) and the Job's prior limits could not be captured to roll back; the Job's limits may be partially applied"
                                    uiNote
                            )

    /// Replace every requested Job limit. A CPU-time limit is established relative to the Job's current
    /// accounting, so this form is used for creation and for an explicit CpuTimeMax change.
    let applyWindowsJobLimits (job: nativeint) (limits: ResourceLimits) : Result<unit, string> =
        match applyWindowsJobLimitsCore false job limits with
        | Error error -> Error error
        | Ok() -> updateIoMax job None limits.IoMax

    /// Replace the non-time limits while preserving an already-running Job's absolute CPU-time deadline.
    /// `JOB_OBJECT_LIMIT_PRESERVE_JOB_TIME` prevents an unrelated live update from granting a fresh CPU
    /// budget; callers use this only when CpuTimeMax remains unchanged and set.
    let applyWindowsJobLimitsPreservingCpuTime (job: nativeint) (limits: ResourceLimits) : Result<unit, string> =
        match applyWindowsJobLimitsCore true job limits with
        | Error error -> Error error
        | Ok() -> updateIoMax job None limits.IoMax

    /// Replace a live Job's limits while retaining the previously recorded I/O policy long enough to
    /// disable an old volume, update a new one, and restore it if a later native block fails. The core
    /// Job limit block is applied first; if I/O fails, that block is reapplied from `previous` so the
    /// caller never observes a mixed resource set.
    let applyWindowsJobLimitsWithPrevious
        (job: nativeint)
        (previous: ResourceLimits)
        (limits: ResourceLimits)
        : Result<unit, string> =
        let preserveJobTime =
            previous.CpuTimeMax.IsSome && limits.CpuTimeMax = previous.CpuTimeMax

        match applyWindowsJobLimitsCore preserveJobTime job limits with
        | Error error -> Error error
        | Ok() ->
            match updateIoMax job previous.IoMax limits.IoMax with
            | Ok() -> Ok()
            | Error ioError ->
                match applyWindowsJobLimitsCore preserveJobTime job previous with
                | Ok() -> Error ioError
                | Error restoreError ->
                    Error(
                        $"failed to apply the Windows Job I/O rate ({ioError}) and could not restore the previous Job limits ({restoreError}); the Job's limits may be partially applied"
                    )

    /// Read back the Job's absolute user-time deadline in 100-nanosecond ticks, when enabled. Windows
    /// normalizes a successful PRESERVE write back to `JOB_OBJECT_LIMIT_JOB_TIME`, so the persisted
    /// deadline — not the transient input flag — is the useful verification seam.
    let queryWindowsJobCpuTimeLimit (job: nativeint) : int64 option =
        match queryExtendedLimit job with
        | Some info when info.BasicLimitInformation.LimitFlags &&& JOB_OBJECT_LIMIT_JOB_TIME <> 0u ->
            Some info.BasicLimitInformation.PerJobUserTimeLimit
        | _ -> None

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
    /// opened — and guarded ONCE, however many times it is disposed. Each of these handles has two
    /// owners: this unwind entry and the per-stream cleanup closure that drops the parent's copy once
    /// the child has inherited it, in an order neither side controls (the cleanup may run before an
    /// exception unwinds, after it, or not at all). Idempotence therefore has to live in the wrapper:
    /// it cannot be recovered by inspection later, because `closeHandleIfValid` only rejects the
    /// never-opened sentinels, and Win32 recycles a handle VALUE the moment it is freed — a second
    /// `CloseHandle` on that value could hit an unrelated object this process has since opened. Same
    /// discipline `spawnDetachedWindows`'s `releaseParentCopies` gets by emptying its list as it
    /// closes. The flag is raised BEFORE the close, so even a close that throws is never retried.
    let private disposableHandle (handle: nativeint) : IDisposable =
        // A ref cell, not a `let mutable`: F# object expressions cannot capture a mutable local.
        // `Interlocked` rather than a plain write because this costs nothing here and keeps the
        // guarantee if a future caller ever disposes from another thread (today every close runs on
        // the spawning thread, under `windowsSpawnLock`).
        let closed = ref 0

        { new IDisposable with
            member _.Dispose() =
                if Interlocked.Exchange(&closed.contents, 1) = 0 then
                    closeHandleIfValid handle }

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
    // EXTENDED_STARTUPINFO_PRESENT + the attribute list). Distinct F# name, same entry point. `lpCommandLine`
    // is a writable unmanaged-buffer `nativeint`, not a managed `string` — see `CreateProcessW` above (T-198).
    [<DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateProcessW")>]
    extern bool private CreateProcessExtended(
        nativeint lpApplicationName,
        nativeint lpCommandLine,
        nativeint lpProcessAttributes,
        nativeint lpThreadAttributes,
        bool bInheritHandles,
        uint32 dwCreationFlags,
        nativeint lpEnvironment,
        string lpCurrentDirectory,
        STARTUPINFOEX& lpStartupInfo,
        PROCESS_INFORMATION& lpProcessInformation
    )

    // ----------------------------------------------------------------------------------
    // ConPTY std-handle binding: the child's stdio must come from the pseudoconsole
    // ----------------------------------------------------------------------------------
    //
    // `bInheritHandles = false` alone does NOT keep the launcher's own stdio away from a ConPTY child, and
    // the two launch environments need DIFFERENT remedies for that — each one empirically necessary, and
    // each one actively harmful in the other environment:
    //
    //  * A CONSOLE-ATTACHED launcher (a developer terminal, a debugger, a console-hosted test runner) has
    //    Windows pre-populate the child's three standard-handle slots from its own console, so the child
    //    attaches to the pseudoconsole and yet keeps writing to the LAUNCHER's console — its output never
    //    reaches the captured merged stream. `STARTF_USESTDHANDLES` with three NULL handles severs that;
    //    ConPTY then installs its own console handles while the child initializes.
    //
    //  * A HEADLESS launcher (a service-hosted CI step, a GUI-subsystem parent, a redirected test host)
    //    must NOT be given that flag: with it the child is stranded with no console output binding at all
    //    (only conhost's own setup frame ever reaches the master), which is the arrangement the Microsoft
    //    ConPTY sample avoids. Without the flag, however, `CreateProcessW` propagates the launcher's OWN
    //    standard-handle VALUES into the child, so a launcher whose stdio is redirected hands the child
    //    that redirect and the child's output escapes the pseudoconsole just the same. The remedy there is
    //    on the launcher side: null THIS process's three standard-handle slots across the spawn call, so
    //    the child propagates null defaults the pseudoconsole overrides — the same end state as the flag
    //    reaches, without the flag.
    //
    // `SetStdHandle` mutates PROCESS-GLOBAL state, so that swap is confined to the one synchronous
    // `CreateProcessExtended` call and runs under `windowsSpawnLock` — which EVERY ProcessKit Windows
    // spawn path already takes (ordinary, ConPTY, detached), so no ProcessKit spawn can observe the
    // temporary nulls, including the `StdioMode.Inherit`/`Command.InheritStdin` paths that read these very
    // slots to duplicate them into a child. The lock cannot reach code outside ProcessKit: a foreign
    // `Process.Start` inheriting stdio, or the FIRST `Console.Out`/`Console.In` access in the process
    // (which caches whatever the slot holds at that moment), can still race that short window from another
    // thread. That residual race is inherent to the mechanism and is documented rather than hidden — a
    // caller who needs strict isolation from such foreign activity should run PTY sessions from a
    // dedicated helper process.

    // Whether the process has a console at all; NULL means it has none. Only the association is read (the
    // returned HWND is never dereferenced), and the call does not set a Win32 error worth reading.
    [<DllImport("kernel32.dll", EntryPoint = "GetConsoleWindow")>]
    extern nativeint private GetConsoleWindow()

    /// Whether THIS process is attached to a console. `GetConsoleWindow` returns NULL exactly when it is
    /// not — the headless case — and non-null under an interactive terminal. Queried per spawn rather than
    /// cached, because a process can gain or lose its console at runtime (`AllocConsole`/`FreeConsole`);
    /// the answer selects which of the two std-handle remedies above a ConPTY spawn needs.
    let launcherHasConsole () : bool = GetConsoleWindow() <> IntPtr.Zero

    // Test seam: observe which std-handle binding a ConPTY spawn actually chose — `true` for the
    // console-attached form (severed through `STARTF_USESTDHANDLES`), `false` for the headless one (this
    // process's own slots nulled across the spawn instead). Production always decides through
    // `launcherHasConsole`; only the (sequential) tests set this, and restore it in a `finally`.
    let mutable windowsPtyStdHandleModeObserverForTests: (bool -> unit) option = None

    /// This process's own three standard-handle slot values, in `STARTUPINFO` order (input, output,
    /// error): what a child created WITHOUT `STARTF_USESTDHANDLES` propagates, and what
    /// `withNulledLauncherStdio` saves and puts back.
    let readLauncherStdHandles () : nativeint * nativeint * nativeint =
        GetStdHandle STD_INPUT_HANDLE, GetStdHandle STD_OUTPUT_HANDLE, GetStdHandle STD_ERROR_HANDLE

    /// Run `spawn` with this process's three standard-handle slots temporarily replaced by NULL, and put
    /// the exact previous values back afterwards — after a successful spawn, after a failed one, and after
    /// an exception alike (`finally`). The headless half of the ConPTY std-handle binding described above:
    /// the caller holds `windowsSpawnLock` for the whole window, and `spawn` is one synchronous
    /// `CreateProcessExtended`, never an `await`.
    ///
    /// `SetStdHandle` writes only the requested slot of this process's own parameter block and can fail
    /// only for an invalid slot id — the three used here are the fixed Win32 constants — so its result is
    /// discarded rather than turned into a spawn failure; on the restore path there would in any case be
    /// nothing to report it through without masking the spawn's own outcome.
    let withNulledLauncherStdio (spawn: unit -> 'T) : 'T =
        // Captured BEFORE anything is mutated, so the restore below puts back exactly what was there.
        let savedInput, savedOutput, savedError = readLauncherStdHandles ()

        try
            SetStdHandle(STD_INPUT_HANDLE, IntPtr.Zero) |> ignore
            SetStdHandle(STD_OUTPUT_HANDLE, IntPtr.Zero) |> ignore
            SetStdHandle(STD_ERROR_HANDLE, IntPtr.Zero) |> ignore
            spawn ()
        finally
            SetStdHandle(STD_INPUT_HANDLE, savedInput) |> ignore
            SetStdHandle(STD_OUTPUT_HANDLE, savedOutput) |> ignore
            SetStdHandle(STD_ERROR_HANDLE, savedError) |> ignore

    /// The ConPTY child's `STARTUPINFOEX`: the pseudoconsole attribute list plus the std-handle decision.
    /// `severConsoleStdHandles` (a console-attached launcher — see `launcherHasConsole`) asks for
    /// `STARTF_USESTDHANDLES` with three NULL handles; a headless launcher leaves the flag clear, so those
    /// three slots are never consulted and the pseudoconsole owns the child's handles instead. See the
    /// section comment above for why the two environments cannot share one form.
    let private conptyStartupInfo (attributeList: nativeint) (severConsoleStdHandles: bool) : STARTUPINFOEX =
        let mutable startup = STARTUPINFOEX()
        startup.StartupInfo.cb <- Marshal.SizeOf<STARTUPINFOEX>()

        if severConsoleStdHandles then
            startup.StartupInfo.dwFlags <- STARTF_USESTDHANDLES
            startup.StartupInfo.hStdInput <- IntPtr.Zero
            startup.StartupInfo.hStdOutput <- IntPtr.Zero
            startup.StartupInfo.hStdError <- IntPtr.Zero

        startup.lpAttributeList <- attributeList
        startup

    /// Test seam: the ConPTY std-handle decision exactly as it reaches `STARTUPINFOEX` — `(dwFlags,
    /// hStdInput, hStdOutput, hStdError)` for a console-attached (`true`) or headless (`false`) launcher.
    /// The struct type is private to this module, so the seam hands back the four fields the decision
    /// writes; production builds its own startup info through the very same function.
    let conptyStdHandleBindingForTests (launcherIsConsoleAttached: bool) : uint32 * nativeint * nativeint * nativeint =
        let startup = conptyStartupInfo IntPtr.Zero launcherIsConsoleAttached

        startup.StartupInfo.dwFlags,
        startup.StartupInfo.hStdInput,
        startup.StartupInfo.hStdOutput,
        startup.StartupInfo.hStdError

    /// `CreateProcessExtended` over a locally-owned `STARTUPINFOEX`/`PROCESS_INFORMATION` pair, returning
    /// `(created, lastWin32Error, processInformation)`. The Win32 error is read HERE, immediately after the
    /// call, rather than by the caller: on the headless path the caller's std-handle restore runs
    /// `SetStdHandle` (another `SetLastError = true` P/Invoke) before the caller could get to it, which
    /// would replace the spawn's own error with the restore's. Everything in here is a P/Invoke over
    /// already-built arguments, so this cannot throw between the caller's command-line buffer allocation
    /// and its free.
    let private createProcessInPseudoConsole
        (commandLineBuffer: nativeint)
        (flags: uint32)
        (environment: nativeint)
        (workingDirectory: string)
        (startupInfo: STARTUPINFOEX)
        : bool * int * PROCESS_INFORMATION =
        let mutable startup = startupInfo
        let mutable info = PROCESS_INFORMATION()

        let created =
            CreateProcessExtended(
                IntPtr.Zero,
                commandLineBuffer,
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

        created, Marshal.GetLastWin32Error(), info

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

    /// The parent's write end of a ConPTY host-input pipe, held open for the pseudoconsole SESSION's whole
    /// lifetime — deliberately NOT owned by whoever writes the child's stdin.
    ///
    /// Closing that pipe is not the ordinary "peer closed stdin" EOF a plain pipe delivers: it tells conhost
    /// the console session is over, and a child that has not reached its first read yet can be torn down
    /// (`CTRL_CLOSE_EVENT`) instead of running to completion. So the pipe belongs to the session — a run with
    /// no stdin writer at all keeps it open, and a writer that finishes or is dropped does not take the
    /// child's terminal with it. It is released exactly once, after the child has exited and the
    /// pseudoconsole has been closed (`closePseudoConsoleOnChildExit`), or by the spawn's failure unwind.
    ///
    /// Those two are independent "this might need closing" paths in an order neither controls, which is why
    /// the release is a shared one-shot — the same discipline this file applies to its raw Win32 handles
    /// (`disposableHandle`). Writers get a NON-owning `ConPtyStdinStream` view over `Stream` instead.
    type internal ConPtyInputKeepalive(pipe: Stream) =
        // A ref cell rather than a `let mutable`, so the guard can be raised with `Interlocked` from either
        // path — the spawning thread's unwind or the thread-pool child-exit callback.
        let released = ref 0

        /// The session's host-input pipe. Writers wrap it in a non-owning view; nobody else disposes it.
        member _.Stream = pipe

        /// Whether the session's host-input pipe has already been released — the child exited (or the run
        /// was torn down), so there is no console left for a stdin write to reach.
        member _.IsReleased = Volatile.Read(&released.contents) <> 0

        /// Close the host-input pipe, exactly once however many paths reach here. The flag is raised BEFORE
        /// the close, so a close that itself throws is never retried on a handle Win32 may have recycled.
        member _.Release() =
            if Interlocked.Exchange(&released.contents, 1) = 0 then
                try
                    pipe.Dispose()
                with
                | :? ObjectDisposedException ->
                    // Already disposed elsewhere (a teardown race): the pipe is gone, which is what this
                    // release wanted. Nothing to recover.
                    ()
                | :? IOException ->
                    // The pipe broke while flushing on dispose (conhost is already gone) — the same
                    // teardown-race close `Pump.disposeQuietly` tolerates. This runs from the child-exit
                    // callback, which must never throw.
                    ()

        interface IDisposable with
            member this.Dispose() = this.Release()

    // The Windows console's own end-of-input gesture — the ConPTY counterpart of a POSIX terminal's
    // `c_cc[VEOF]`: Ctrl-Z (SUB, 0x1A) followed by the carriage return a terminal sends for Enter. A console
    // read in cooked (line) mode ends at the Ctrl-Z and the child's next read returns zero bytes: the EOF
    // `copy con`, `sort`, and every `ReadToEnd` on a console have always been ended with. Both bytes go out
    // in ONE write so the Enter can neither be separated from the Ctrl-Z it submits nor race a child that
    // acts on the first byte. Like the POSIX end-of-input character, it only reaches a child whose console
    // input is still in cooked mode — one that switched CONIN$ to raw mode reads it as ordinary input, which
    // is the console's contract rather than something this can paper over.
    let private conptyEndOfInputGesture = [| 0x1Auy; 0x0Duy |]

    // `HRESULT_FROM_WIN32` (facility 7) for the three Win32 write errors that all mean the same thing: the
    // host-input pipe has no reader any more, because conhost let go of its end when the pseudoconsole was
    // closed. ERROR_BROKEN_PIPE (109), ERROR_NO_DATA (232), ERROR_PIPE_NOT_CONNECTED (233) — the codes the
    // .NET pipe layer wraps into an `IOException` for a write whose peer is gone.
    let private isHostInputHangup (ex: IOException) =
        match uint32 ex.HResult with
        | 0x8007006Du
        | 0x800700E8u
        | 0x800700E9u -> true
        | _ -> false

    /// The parent-side stdin writer for a ConPTY run: a NON-owning view over the session's host-input pipe.
    ///
    /// It writes through `keepalive.Stream` and never closes it, because on Windows closing that pipe ends
    /// the console session rather than delivering a stdin EOF (see `ConPtyInputKeepalive`). Ending this
    /// stdin is therefore not a close: `FinishAsync` delivers the console's own end-of-input gesture
    /// (Ctrl-Z + Enter) so a child reading to EOF actually sees one, and the session's pipe stays open until
    /// the child exits.
    ///
    /// The finish is once-only and its outcome is honest: the first call claims it and every later caller
    /// gets that same delivery task (including its failure), writes are refused from the claim onwards, and
    /// a genuine delivery failure faults the returned task instead of silently leaving the child hanging.
    /// The two cases where end of input is moot rather than lost complete successfully — the session's pipe
    /// has already been released (the child exited, or the run's own teardown got there first), or the write
    /// reports that the pipe has no reader any more, which is the same thing observed a moment before our
    /// own teardown reached it.
    type internal ConPtyStdinStream(keepalive: ConPtyInputKeepalive) =
        inherit Stream()

        let pipe = keepalive.Stream

        // Serializes payload writes against the once-only end-of-input delivery, so the gesture can never
        // overtake a write that was already admitted, and a write that arrives after it is refused rather
        // than trailing past the end of input the child has seen. Held across one stream write only, never
        // across a caller's whole session. Not a hot path: one stdin view per ConPTY run, caller-paced.
        let writeGate = new SemaphoreSlim(1, 1)

        // Guards the finish claim/memo and the released flag; never held across I/O.
        let claimGate = obj ()
        let mutable finished = false
        let mutable released = false
        let mutable finishTask: Task option = None

        // Refuse a write the same way every closed .NET stream does — the `ObjectDisposedException` the
        // POSIX pty stdin view and a plain closed stdin pipe both raise — rather than inventing a third
        // shape for the one public `ProcessStdin` contract.
        let ensureWritable () =
            lock claimGate (fun () ->
                if finished then
                    raise (
                        ObjectDisposedException(
                            nameof ConPtyStdinStream,
                            "the child's stdin has been finished: the console's end-of-input gesture was already delivered, so further input would trail past the end of input the child has seen"
                        )
                    )
                elif released then
                    raise (
                        ObjectDisposedException(
                            nameof ConPtyStdinStream,
                            "this stdin writer has been released; the ConPTY session's host-input pipe belongs to the run, not to this writer"
                        )
                    ))

        // One admitted synchronous write: take the gate, check the gate-protected state, hand the bytes over.
        let writeThrough (write: unit -> unit) =
            writeGate.Wait()

            try
                ensureWritable ()
                write ()
            finally
                writeGate.Release() |> ignore

        // The asynchronous counterpart. The refusal check happens AFTER the gate is taken, so a write that
        // loses the race to the end-of-input delivery is refused instead of landing behind it.
        let writeThroughAsync (cancellationToken: CancellationToken) (write: unit -> Task) : Task =
            task {
                do! writeGate.WaitAsync cancellationToken

                try
                    ensureWritable ()
                    do! write ()
                finally
                    writeGate.Release() |> ignore
            }

        /// Whether the logical end of input has been claimed — after which this view refuses writes.
        member _.IsFinished = lock claimGate (fun () -> finished)

        override _.CanRead = false
        override _.CanSeek = false
        override _.CanWrite = true

        override _.Length = raise (NotSupportedException "a ConPTY stdin writer has no length")

        override _.Position
            with get () = raise (NotSupportedException "a ConPTY stdin writer has no position")
            and set (_: int64) = raise (NotSupportedException "a ConPTY stdin writer has no position")

        override _.Read(_buffer: byte[], _offset: int, _count: int) =
            raise (NotSupportedException "a ConPTY stdin writer is write-only")

        override _.Seek(_offset: int64, _origin: SeekOrigin) =
            raise (NotSupportedException "a ConPTY stdin writer is not seekable")

        override _.SetLength(_value: int64) =
            raise (NotSupportedException "a ConPTY stdin writer has no length")

        override _.Flush() = pipe.Flush()

        override _.FlushAsync(cancellationToken: CancellationToken) = pipe.FlushAsync cancellationToken

        override _.Write(buffer: byte[], offset: int, count: int) =
            writeThrough (fun () -> pipe.Write(buffer, offset, count))

        override _.WriteAsync(buffer: byte[], offset: int, count: int, cancellationToken: CancellationToken) : Task =
            writeThroughAsync cancellationToken (fun () -> pipe.WriteAsync(buffer, offset, count, cancellationToken))

        override _.WriteAsync(buffer: ReadOnlyMemory<byte>, cancellationToken: CancellationToken) : ValueTask =
            ValueTask(
                writeThroughAsync cancellationToken (fun () -> pipe.WriteAsync(buffer, cancellationToken).AsTask())
            )

        /// Deliver the console's end-of-input gesture, leaving the session's pipe open. Runs the claim's
        /// delivery only; see the type doc for the contract. Prefer the `IStdinFinisher` interface at call
        /// sites — this concrete member exists so the behaviour can be exercised directly in tests.
        member this.FinishAsync() : Task =
            lock claimGate (fun () ->
                match finishTask with
                | Some task ->
                    // Every caller observes the one delivery task, including its eventual failure.
                    task
                | None ->
                    finished <- true
                    // Captured under the claim rather than read inside the delivery: a writer the run's own
                    // teardown has already released has nothing left to end, and `ProcessStdin.FinishAsync`
                    // promises to stay quiet there instead of pushing input into a session it no longer owns.
                    let task = this.DeliverEndOfInput released
                    finishTask <- Some task
                    task)

        // The delivery itself. Failures propagate to the `FinishAsync` task except for the cases where end of
        // input is moot rather than undelivered (see the handlers).
        member private this.DeliverEndOfInput(alreadyReleased: bool) : Task =
            task {
                // The claim already stops new writes; this waits out any write admitted before it, so the
                // gesture can never overtake input the caller had already handed over.
                do! writeGate.WaitAsync()

                try
                    try
                        if not (alreadyReleased || keepalive.IsReleased) then
                            do! pipe.WriteAsync(conptyEndOfInputGesture.AsMemory(), CancellationToken.None)
                            do! pipe.FlushAsync CancellationToken.None
                    with
                    | :? ObjectDisposedException ->
                        // The session's pipe was released while this delivery was in flight (the child
                        // exited, or the run's own teardown got there first). There is no console left to
                        // hand an end of input to, exactly as closing a pipe whose peer is gone is not a
                        // failed close.
                        ()
                    | :? IOException as ex when this.IsHangup ex ->
                        // The console host has already let go of the read end: the same "the terminal is
                        // gone" case as above, observed a moment before our own child-exit teardown reached
                        // it. Any OTHER I/O failure is a genuinely undelivered end of input and propagates.
                        ()
                finally
                    writeGate.Release() |> ignore
            }

        // Whether an I/O failure of the gesture write means there is no console left to receive an end of
        // input (moot) rather than one that did not get it (a real failure the caller must hear about).
        // Three independent signals, because no single one covers the whole window: the session's pipe has
        // been released by the child-exit teardown; the write itself reported a Win32 hangup code; or the
        // .NET pipe has already latched into its broken state from an EARLIER write (the stdin feeder's),
        // whose follow-up exception carries the generic I/O HResult instead of a Win32 one.
        member private _.IsHangup(ex: IOException) =
            keepalive.IsReleased
            || isHostInputHangup ex
            || (match pipe with
                | :? PipeStream as hostInput -> not hostInput.IsConnected
                | _ -> false)

        override _.Dispose(disposing) =
            lock claimGate (fun () -> released <- true)

            // Deliberately does NOT dispose `pipe`: this view is non-owning, and closing the session's
            // host-input pipe here would end the child's console session instead of its stdin. The gate is
            // left undisposed too — it holds no unmanaged resource (its wait handle is never materialised),
            // and disposing it under an in-flight finish would turn a quiet teardown into a fault.
            base.Dispose disposing

        interface IStdinFinisher with
            member this.FinishAsync() : Task = this.FinishAsync()

    /// Close the pseudoconsole `hPC` — tearing down the conhost sidecar (which lives OUTSIDE the Job) — and
    /// release the session's host-input pipe, once the child `hProcess` exits. Closing the pseudoconsole also
    /// closes the merged-output pipe's write end, so the parent's read reaches EOF and the capture/streaming
    /// pumps conclude (they never would while conhost holds that write handle open). Waits on our OWN
    /// duplicate of the process handle (the backend may close its copy on reap) via a thread-pool registered
    /// wait — one pool wait thread serves ~63 handles, so no dedicated thread is parked per PTY child. `hPC`
    /// is closed exactly once, by this one-shot wait — never elsewhere (no double-close) — and `hostInput` is
    /// released exactly once by its own shared guard. Returns `false` only if the initial handle duplication
    /// fails (near-impossible for a just-created process).
    let private closePseudoConsoleOnChildExit
        (hProcess: nativeint)
        (hPC: nativeint)
        (hostInput: ConPtyInputKeepalive)
        : bool =
        let current = GetCurrentProcess()
        let mutable duplicate = IntPtr.Zero

        if not (DuplicateHandle(current, hProcess, current, &duplicate, 0u, false, DUPLICATE_SAME_ACCESS)) then
            false
        else
            let waitHandle =
                new OwnedProcessWait(new SafeWaitHandle(duplicate, ownsHandle = true))

            let tcs = TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)

            // Fires once, when the child exits: close the pseudoconsole (conhost flushes its final output
            // to the pipe, then exits), release the session's host-input pipe now that there is no child
            // left for its closure to disturb, then let the continuation unregister the wait and release
            // the duplicate. Publishing the registration before attaching the continuation makes the
            // unregister race-free even if the child had already exited when we registered (mirrors
            // `waitWindows`). Neither step can throw into the pool: `ClosePseudoConsole` is a void Win32
            // call and `Release` swallows its own teardown-race exceptions.
            let callback =
                WaitOrTimerCallback(fun _ _ ->
                    ClosePseudoConsole hPC
                    hostInput.Release()
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

        // Decide the launch (PATHEXT / effective-child-`PATH` substitution / cmd.exe batch wrapper —
        // T-181/T-339) and build the command line up front: an unsafe batch argument, or a program the
        // command's own overridden child `PATH` does not hold, is refused here, BEFORE the pseudoconsole
        // or any pipe is allocated, so a refusal leaks nothing.
        match buildWindowsCommandLine command with
        | Error error -> Error error
        | Ok commandLine ->

            try
                // Two async pipe pairs. Parent keeps the input WRITE end (the pseudoconsole's host input) and
                // the output READ end (the single merged terminal stream); the child-side ends are handed to
                // CreatePseudoConsole and closed once it has duplicated them into the conhost sidecar.
                //
                // The host-input end goes straight into a session keepalive: it stays open for as long as the
                // console session does, whether or not this run ever hands out a stdin writer, because closing
                // it asks conhost to end the session rather than delivering a stdin EOF. The keepalive (not
                // the raw stream) is what goes on the unwind list, so the unwind and the child-exit teardown
                // share ONE one-shot release.
                let inServer, inClient = createAsyncPipePair PipeDirection.Out
                let hostInput = new ConPtyInputKeepalive(inServer)
                createdPipes.Add hostInput
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
                        // A ConPTY child's std handles come from the pseudoconsole, never from this process
                        // — the fundamental divergence from the pipe path — but a console-attached and a
                        // headless launcher need different remedies to guarantee it (see "ConPTY std-handle
                        // binding" above). Decided once here, then applied in the two matching places: the
                        // startup info's `STARTF_USESTDHANDLES` for a console-attached launcher, and the
                        // launcher-side null swap around the spawn call itself for a headless one.
                        let consoleAttached = launcherHasConsole ()
                        let startup = conptyStartupInfo attrList consoleAttached

                        windowsPtyStdHandleModeObserverForTests
                        |> Option.iter (fun observer -> observer consoleAttached)

                        let workingDirectory =
                            config.WorkingDirectory |> Option.defaultWith Directory.GetCurrentDirectory

                        let environment = buildWindowsEnvironment command

                        let flags =
                            // Spawn SUSPENDED so the child is assigned to the Job before it runs (proven
                            // containment). EXTENDED_STARTUPINFO_PRESENT selects the STARTUPINFOEX form. A
                            // ConPTY child always gets its own console process group: the isolated terminal
                            // has no useful shared-group semantics, and the flag prevents a stray CTRL+C
                            // broadcast on the caller's console from terminating it. WindowsCtrlSignals
                            // remains only the opt-in registration for ProcessKit's targeted CTRL+BREAK.
                            EXTENDED_STARTUPINFO_PRESENT
                            ||| CREATE_SUSPENDED
                            ||| CREATE_NEW_PROCESS_GROUP
                            ||| (if environment = IntPtr.Zero then
                                     0u
                                 else
                                     CREATE_UNICODE_ENVIRONMENT)
                            ||| (if config.CreateNoWindow then CREATE_NO_WINDOW else 0u)
                            ||| (match config.Priority with
                                 | Some priority -> PriorityMapping.windowsCreationFlag priority
                                 | None -> 0u)

                        windowsCreationFlagsObserverForTests
                        |> Option.iter (fun observer -> observer (true, flags))

                        // A PRIVATE, writable copy of the command line: `CreateProcessW` may patch this buffer
                        // in place while probing executable candidates, so it must never be the memory of a
                        // managed `string` (a possibly interned literal) — see the binding above (T-198).
                        let commandLineBuffer = Marshal.StringToHGlobalUni commandLine

                        // The spawn itself, and on a headless launcher the null std-handle swap around it —
                        // as narrow as the mechanism allows: the swap covers this one synchronous call and
                        // nothing else, and restores the slots however the call ends. Both branches make
                        // only P/Invoke calls, so nothing between the buffer allocation above and its free
                        // below can throw. The Win32 error comes back WITH the result because the restore's
                        // own `SetStdHandle` would otherwise overwrite it.
                        let created, lastError, info =
                            if consoleAttached then
                                createProcessInPseudoConsole
                                    commandLineBuffer
                                    flags
                                    environment
                                    workingDirectory
                                    startup
                            else
                                withNulledLauncherStdio (fun () ->
                                    createProcessInPseudoConsole
                                        commandLineBuffer
                                        flags
                                        environment
                                        workingDirectory
                                        startup)

                        // Free the writable command-line copy now: `CreateProcess` has finished reading (and
                        // restoring) it by the time it returns, and no throwing code runs between its
                        // allocation and here, so there is nothing to leak.
                        Marshal.FreeHGlobal commandLineBuffer

                        if environment <> IntPtr.Zero then
                            Marshal.FreeHGlobal environment

                        cleanupInitializedScratch ()

                        if not created then
                            ClosePseudoConsole hPC
                            pendingPseudoConsole <- IntPtr.Zero
                            disposeCreatedPipes ()

                            if lastError = ERROR_FILE_NOT_FOUND || lastError = ERROR_PATH_NOT_FOUND then
                                Error(notFoundFromSpawnFailure command)
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

                            if not (closePseudoConsoleOnChildExit info.hProcess hPC hostInput) then
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

                                windowsCtrlGroupObserverForTests
                                |> Option.iter (fun observer -> observer (true, config.WindowsCtrlSignals))

                                let stdinStream =
                                    if stdinPipeKept then
                                        // A NON-owning view: finishing or dropping the writer must not take
                                        // the console session with it (see `ConPtyStdinStream`).
                                        Some(new ConPtyStdinStream(hostInput) :> Stream)
                                    else
                                        // No feeder/interactive writer — and deliberately NOT a close of the
                                        // host-input pipe. Closing it here asks conhost to end the console
                                        // session, which can hit a child that has not run a single
                                        // instruction yet with a CTRL_CLOSE_EVENT; the keepalive holds it
                                        // until the child exits instead. A child that reads to EOF under a
                                        // PTY needs a stdin source or an explicit finish to see one, exactly
                                        // as on a POSIX pty, whose master the merged-output stream keeps open
                                        // for the same reason.
                                        None

                                Ok
                                    { Handle = info.hProcess
                                      // One merged terminal stream (D3): stdout carries all output, no stderr.
                                      Stdout = Some(outServer :> Stream)
                                      Stderr = None
                                      Stdin = stdinStream
                                      ExtraFds = []
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

    // ----------------------------------------------------------------------------------
    // Token hardening: a restricted / lowered-integrity primary token for the child
    // ----------------------------------------------------------------------------------
    //
    // `Command.WindowsRestrictedToken` and `Command.WindowsIntegrityLevel` are the Windows counterpart of
    // the POSIX `Uid`/`Gid`/`Groups` drop: instead of changing WHO the child runs as, they hand it a
    // weakened copy of this process's own primary token, so it keeps the caller's identity but loses
    // privileges (`CreateRestrictedToken` with `DISABLE_MAX_PRIVILEGE`) and/or the right to write to
    // anything labelled above its mandatory integrity level (`SetTokenInformation(TokenIntegrityLevel)`).
    // A child started under such a token goes through `CreateProcessAsUserW` rather than `CreateProcessW`;
    // everything else about the spawn — the suspended→assign-to-Job→resume containment dance, the stdio
    // handles, the environment block, the command-line buffer — is byte-for-byte the ordinary path.
    //
    // Both tokens are derived from the caller's OWN token, which is what keeps this an unprivileged
    // operation: Windows lets a process assign a token derived from its own without
    // `SE_ASSIGNPRIMARYTOKEN_NAME`. A host that nevertheless refuses (a locked-down policy) surfaces as a
    // typed `ProcessError.Spawn` naming the missing privilege — never a silent fallback to an unhardened
    // child, which would be exactly the "you asked for a sandbox and did not get one" failure this whole
    // feature exists to prevent.

    // Token access rights (winnt.h). Deliberately the exact set needed, not `TOKEN_ALL_ACCESS`:
    // duplicate/query the source, adjust the copy's default label, and assign it as a primary token.
    [<Literal>]
    let private TOKEN_ASSIGN_PRIMARY = 0x0001u

    [<Literal>]
    let private TOKEN_DUPLICATE = 0x0002u

    [<Literal>]
    let private TOKEN_QUERY = 0x0008u

    [<Literal>]
    let private TOKEN_ADJUST_DEFAULT = 0x0080u

    // `CreateRestrictedToken` flag: disable every privilege in the new token except
    // `SeChangeNotifyPrivilege` (which Windows requires for ordinary path traversal).
    [<Literal>]
    let private DISABLE_MAX_PRIVILEGE = 0x1u

    // `SECURITY_IMPERSONATION_LEVEL.SecurityImpersonation` and `TOKEN_TYPE.TokenPrimary`. The level is
    // ignored for a primary token but must still be a valid enumeration value.
    [<Literal>]
    let private SecurityImpersonation = 2

    [<Literal>]
    let private TokenPrimary = 1

    // `TOKEN_INFORMATION_CLASS.TokenIntegrityLevel` and the `SE_GROUP_INTEGRITY` attribute a mandatory
    // label carries.
    [<Literal>]
    let private TokenIntegrityLevel = 25

    [<Literal>]
    let private SE_GROUP_INTEGRITY = 0x00000020u

    // Returned by `CreateProcessAsUser` on a host whose policy denies assigning this primary token.
    [<Literal>]
    let private ERROR_PRIVILEGE_NOT_HELD = 1314

    [<StructLayout(LayoutKind.Sequential)>]
    type private SID_AND_ATTRIBUTES =
        struct
            val mutable Sid: nativeint
            val mutable Attributes: uint32
        end

    [<StructLayout(LayoutKind.Sequential)>]
    type private TOKEN_MANDATORY_LABEL =
        struct
            val mutable Label: SID_AND_ATTRIBUTES
        end

    [<DllImport("advapi32.dll", SetLastError = true)>]
    extern bool private OpenProcessToken(nativeint ProcessHandle, uint32 DesiredAccess, nativeint& TokenHandle)

    [<DllImport("advapi32.dll", SetLastError = true)>]
    extern bool private CreateRestrictedToken(
        nativeint ExistingTokenHandle,
        uint32 Flags,
        uint32 DisableSidCount,
        nativeint SidsToDisable,
        uint32 DeletePrivilegeCount,
        nativeint PrivilegesToDelete,
        uint32 RestrictedSidCount,
        nativeint SidsToRestrict,
        nativeint& NewTokenHandle
    )

    [<DllImport("advapi32.dll", SetLastError = true)>]
    extern bool private DuplicateTokenEx(
        nativeint hExistingToken,
        uint32 dwDesiredAccess,
        nativeint lpTokenAttributes,
        int ImpersonationLevel,
        int TokenType,
        nativeint& phNewToken
    )

    [<DllImport("advapi32.dll", SetLastError = true)>]
    extern bool private SetTokenInformation(
        nativeint TokenHandle,
        int TokenInformationClass,
        nativeint TokenInformation,
        uint32 TokenInformationLength
    )

    // `StringSid` is a read-only `LPCWSTR` input — unlike `CreateProcess*`'s `lpCommandLine` (T-198) this
    // API never writes through it, so an ordinary marshalled managed string is correct here.
    [<DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)>]
    extern bool private ConvertStringSidToSidW(string StringSid, nativeint& Sid)

    [<DllImport("advapi32.dll")>]
    extern uint32 private GetLengthSid(nativeint pSid)

    [<DllImport("kernel32.dll")>]
    extern nativeint private LocalFree(nativeint hMem)

    // Same shape as `CreateProcessW` with the token in front. `lpCommandLine` is `LPWSTR` here too — the
    // OS may patch it in place — so it is a `nativeint` into a private unmanaged buffer, never a managed
    // string (T-198); the call sites share the very same buffer they build for `CreateProcessW`.
    [<DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateProcessAsUserW")>]
    extern bool private CreateProcessAsUserW(
        nativeint hToken,
        nativeint lpApplicationName,
        nativeint lpCommandLine,
        nativeint lpProcessAttributes,
        nativeint lpThreadAttributes,
        bool bInheritHandles,
        uint32 dwCreationFlags,
        nativeint lpEnvironment,
        string lpCurrentDirectory,
        STARTUPINFO& lpStartupInfo,
        PROCESS_INFORMATION& lpProcessInformation
    )

    /// The well-known SID of each mandatory integrity level (`S-1-16-<rid>`). Locale-independent and
    /// stable across Windows versions, which is why the label is built from the SID string rather than
    /// looked up by name.
    let private integrityLevelSid (level: WindowsIntegrityLevel) : string =
        match level with
        | WindowsIntegrityLevel.Untrusted -> "S-1-16-0"
        | WindowsIntegrityLevel.Low -> "S-1-16-4096"
        | WindowsIntegrityLevel.Medium -> "S-1-16-8192"

    /// Lower `token`'s mandatory integrity level to `level`. The SID comes from `ConvertStringSidToSidW`
    /// (allocated by the OS with `LocalAlloc`, so freed with `LocalFree`) and is written through a
    /// `TOKEN_MANDATORY_LABEL` whose length must include the variable-size SID that follows it. Every
    /// allocation is released on both the success and the failure path.
    let private applyIntegrityLevel (token: nativeint) (level: WindowsIntegrityLevel) : Result<unit, string> =
        let mutable sid = IntPtr.Zero

        if not (ConvertStringSidToSidW(integrityLevelSid level, &sid)) then
            Error(
                $"could not build the {level} integrity-level SID: {Win32Exception(Marshal.GetLastWin32Error()).Message}"
            )
        else
            let labelSize = Marshal.SizeOf<TOKEN_MANDATORY_LABEL>()
            let buffer = Marshal.AllocHGlobal labelSize

            try
                let mutable label = TOKEN_MANDATORY_LABEL()
                label.Label.Sid <- sid
                label.Label.Attributes <- SE_GROUP_INTEGRITY
                Marshal.StructureToPtr<TOKEN_MANDATORY_LABEL>(label, buffer, false)

                // The information length covers the fixed struct plus the SID it points at — the
                // documented contract for `TokenIntegrityLevel`.
                let informationLength = uint32 labelSize + GetLengthSid sid

                if SetTokenInformation(token, TokenIntegrityLevel, buffer, informationLength) then
                    Ok()
                else
                    // Captured inline, before the `finally` runs a P/Invoke that could reset it.
                    Error(
                        $"could not lower the child token to {level} integrity: {Win32Exception(Marshal.GetLastWin32Error()).Message}"
                    )
            finally
                Marshal.FreeHGlobal buffer
                LocalFree sid |> ignore

    /// The hardened PRIMARY token a child asking for `WindowsRestrictedToken` and/or
    /// `WindowsIntegrityLevel` must be started under, or `IntPtr.Zero` when it asked for neither (the
    /// ordinary `CreateProcessW` path). The caller owns the returned handle and must close it after the
    /// spawn; every intermediate handle and allocation is released here, on success and failure alike.
    ///
    /// A restricted token is `CreateRestrictedToken(DISABLE_MAX_PRIVILEGE)` over this process's own
    /// token — every privilege but `SeChangeNotifyPrivilege` disabled, identity untouched. Without that
    /// flag the token is a plain primary duplicate, so an integrity-only request never mutates the
    /// caller's OWN token (which relabelling in place would, permanently, for the whole process).
    let private buildHardenedToken (config: CommandConfig) : Result<nativeint, string> =
        if not config.WindowsRestrictedToken && config.WindowsIntegrityLevel.IsNone then
            Ok IntPtr.Zero
        else
            let mutable selfToken = IntPtr.Zero

            let access =
                TOKEN_DUPLICATE
                ||| TOKEN_QUERY
                ||| TOKEN_ASSIGN_PRIMARY
                ||| TOKEN_ADJUST_DEFAULT

            if not (OpenProcessToken(GetCurrentProcess(), access, &selfToken)) then
                Error(
                    $"could not open this process's own token to derive a hardened one: {Win32Exception(Marshal.GetLastWin32Error()).Message}"
                )
            else
                try
                    let mutable childToken = IntPtr.Zero

                    let derived =
                        if config.WindowsRestrictedToken then
                            if
                                CreateRestrictedToken(
                                    selfToken,
                                    DISABLE_MAX_PRIVILEGE,
                                    0u,
                                    IntPtr.Zero,
                                    0u,
                                    IntPtr.Zero,
                                    0u,
                                    IntPtr.Zero,
                                    &childToken
                                )
                            then
                                Ok childToken
                            else
                                Error(
                                    $"could not create a restricted token for the child: {Win32Exception(Marshal.GetLastWin32Error()).Message}"
                                )
                        elif
                            DuplicateTokenEx(
                                selfToken,
                                access,
                                IntPtr.Zero,
                                SecurityImpersonation,
                                TokenPrimary,
                                &childToken
                            )
                        then
                            Ok childToken
                        else
                            Error(
                                $"could not duplicate this process's token for the child: {Win32Exception(Marshal.GetLastWin32Error()).Message}"
                            )

                    match derived with
                    | Error message -> Error message
                    | Ok token ->
                        match config.WindowsIntegrityLevel with
                        | None -> Ok token
                        | Some level ->
                            match applyIntegrityLevel token level with
                            | Ok() -> Ok token
                            | Error message ->
                                // The token is unusable as requested; close it rather than hand back a
                                // half-hardened one the caller might spawn under.
                                closeHandleIfValid token
                                Error message
                finally
                    closeHandleIfValid selfToken

    /// The result of the token-aware child creation: the hardened token could not be built (nothing was
    /// spawned), or `CreateProcess*` ran and reported this success flag plus the Win32 error captured
    /// immediately after it.
    type private ChildCreation =
        | TokenFailed of Reason: string
        | Created of Succeeded: bool * LastError: int

    /// Create the child through `CreateProcessAsUserW` with a hardened token when the command asked for
    /// one, and through the ordinary `CreateProcessW` otherwise — the single seam both Windows spawn
    /// paths (contained and detached) share, so the hardening cannot be honoured on one and silently
    /// dropped on the other. The token's whole lifetime lives inside this function: built immediately
    /// before the call and closed immediately after it (the child holds its own reference by then), with
    /// no throwing code in between, so it can neither leak nor outlive the spawn that consumes it.
    let private createChildProcess
        (config: CommandConfig)
        (commandLineBuffer: nativeint)
        (creationFlags: uint32)
        (environment: nativeint)
        (workingDirectory: string)
        (startup: byref<STARTUPINFO>)
        (info: byref<PROCESS_INFORMATION>)
        : ChildCreation =
        match buildHardenedToken config with
        | Error reason -> TokenFailed reason
        | Ok token ->
            let created =
                if token = IntPtr.Zero then
                    CreateProcessW(
                        IntPtr.Zero,
                        commandLineBuffer,
                        IntPtr.Zero,
                        IntPtr.Zero,
                        true,
                        creationFlags,
                        environment,
                        workingDirectory,
                        &startup,
                        &info
                    )
                else
                    CreateProcessAsUserW(
                        token,
                        IntPtr.Zero,
                        commandLineBuffer,
                        IntPtr.Zero,
                        IntPtr.Zero,
                        true,
                        creationFlags,
                        environment,
                        workingDirectory,
                        &startup,
                        &info
                    )

            // Read the error BEFORE closing the token: `CloseHandle` is itself a P/Invoke and would reset
            // the thread's last-error value the caller needs to classify this spawn.
            let lastError = Marshal.GetLastWin32Error()
            closeHandleIfValid token
            Created(created, lastError)

    /// Turn a failed hardened-token spawn into an honest, typed `ProcessError`. `ERROR_PRIVILEGE_NOT_HELD`
    /// is the one failure worth naming specifically: it means this host's policy refuses to let the
    /// process assign the token it derived, so the requested hardening cannot be applied here at all —
    /// reported as such rather than as an opaque Win32 message, and never downgraded to an unhardened
    /// child.
    let private hardenedSpawnError (command: Command) (lastError: int) : ProcessError =
        if lastError = ERROR_PRIVILEGE_NOT_HELD then
            ProcessError.Spawn(
                command.Program,
                "this host refuses to start a process under the hardened token (CreateProcessAsUser reported ERROR_PRIVILEGE_NOT_HELD); the requested WindowsRestrictedToken/WindowsIntegrityLevel could not be applied"
            )
        else
            ProcessError.Spawn(command.Program, Win32Exception(lastError).Message)

    /// Whether `command` asked for any Windows token hardening — the switch between the plain
    /// `CreateProcessW` spawn and the `CreateProcessAsUser` one, and the reason a failed spawn is
    /// classified by `hardenedSpawnError`.
    let private wantsHardenedToken (config: CommandConfig) : bool =
        config.WindowsRestrictedToken || config.WindowsIntegrityLevel.IsSome

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

        // Decide the launch (PATHEXT / effective-child-`PATH` substitution / cmd.exe batch wrapper —
        // T-181/T-339) and build the command line up front: an unsafe batch argument, or a program the
        // command's own overridden child `PATH` does not hold, is refused here, BEFORE any pipe/handle is
        // allocated.
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
                // `stdinCleanup` is that drop for the inherit path (a no-op for the pipe path, whose ends
                // are closed as streams) — the SAME one-shot guard that sits in `createdPipes`, so the
                // unwind can never close the handle a second time. Mirrors `setupOut`'s shape below.
                let stdinChild, inStreams, stdinCleanup =
                    if stdinInherit then
                        let handle = inheritableStdHandle STD_INPUT_HANDLE

                        if not (isValidHandle handle) then
                            // Same rationale as `setupOut`'s Inherit branch: a failed `GetStdHandle`/
                            // `DuplicateHandle` (e.g. no console and stdin not redirected) must fail the
                            // spawn, not silently hand the child a broken std input handle.
                            let message = Win32Exception(Marshal.GetLastWin32Error()).Message

                            failwith
                                $"could not duplicate an inheritable copy of the parent's standard input handle: {message}"

                        let guard = disposableHandle handle
                        createdPipes.Add guard
                        handle, None, (fun () -> guard.Dispose())
                    else
                        let inServer, inClient = createAsyncPipePair PipeDirection.Out
                        createdPipes.Add inServer
                        createdPipes.Add inClient
                        inClient.SafePipeHandle.DangerousGetHandle(), Some(inServer, inClient), (fun () -> ())

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
                        // The cleanup below closes it THROUGH that same one-shot entry, so whichever runs
                        // first is the only close — the unwind never re-closes a recycled handle value.
                        let guard = disposableHandle handle
                        createdPipes.Add guard
                        handle, None, (fun () -> guard.Dispose())
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
                            // handle has not been handed to a child yet and must not leak. Closed through that
                            // same one-shot entry below, so the unwind never closes it twice.
                            let guard = disposableHandle handle
                            createdPipes.Add guard
                            handle, None, (fun () -> guard.Dispose())
                        | StdioMode.Inherit ->
                            let handle = inheritableStdHandle stdHandleId

                            if not (isValidHandle handle) then
                                // Same rationale as the `Null` branch above: `GetStdHandle`/`DuplicateHandle`
                                // failing (e.g. no console and this stream not redirected) must fail the spawn,
                                // not silently hand the child a broken std handle.
                                let message = Win32Exception(Marshal.GetLastWin32Error()).Message

                                failwith
                                    $"could not duplicate an inheritable copy of the parent's std handle: {message}"

                            // Same one-shot entry/cleanup pairing as the two branches above.
                            let guard = disposableHandle handle
                            createdPipes.Add guard
                            handle, None, (fun () -> guard.Dispose())

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

                windowsCreationFlagsObserverForTests
                |> Option.iter (fun observer -> observer (false, flags))

                // A PRIVATE, writable copy of the command line: `CreateProcessW` may patch this buffer in
                // place while probing executable candidates, so it must never be the memory of a managed
                // `string` (a possibly interned literal) — see the binding above (T-198).
                let commandLineBuffer = Marshal.StringToHGlobalUni commandLine

                // `CreateProcessW`, or `CreateProcessAsUserW` under a restricted / lowered-integrity token
                // when the command asked for one (the token is built and closed inside this call).
                let creation =
                    createChildProcess config commandLineBuffer flags environment workingDirectory &startup &info

                let struct (created, lastError) =
                    match creation with
                    | Created(succeeded, error) -> struct (succeeded, error)
                    // The hardened token could not be built, so nothing was spawned: report the reason as
                    // the spawn failure instead of quietly starting an unhardened child. `lastError = 0`
                    // routes past the NotFound/hardened classification below to this message.
                    | TokenFailed _ -> struct (false, 0)

                // Free the writable command-line copy now: `CreateProcess` has finished reading (and
                // restoring) it by the time it returns, and no throwing code runs between its allocation and
                // here, so there is nothing to leak.
                Marshal.FreeHGlobal commandLineBuffer

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
                        // Inherit: no pipe — just close the inheritable duplicate of the parent's std input,
                        // through its one-shot guard (so the unwind below cannot close it again).
                        stdinCleanup ()

                if not created then
                    releaseStdio ()

                    match creation with
                    | TokenFailed reason -> Error(ProcessError.Spawn(command.Program, reason))
                    | Created _ ->
                        if lastError = ERROR_FILE_NOT_FOUND || lastError = ERROR_PATH_NOT_FOUND then
                            Error(notFoundFromSpawnFailure command)
                        elif wantsHardenedToken config then
                            Error(hardenedSpawnError command lastError)
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

                    windowsCtrlGroupObserverForTests
                    |> Option.iter (fun observer -> observer (false, config.WindowsCtrlSignals))

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
                            // drop the parent's inheritable duplicate — through its one-shot guard, so a
                            // throw later on this success path cannot make the unwind close it again. There is
                            // no parent-side stdin stream.
                            stdinCleanup ()
                            None

                    Ok
                        { Handle = info.hProcess
                          Stdout = outStream
                          Stderr = errStream
                          Stdin = stdinStream
                          ExtraFds = []
                          WindowsCtrlGroup = config.WindowsCtrlSignals
                          // Not a PTY run — no pseudoconsole to resize (`ResizeAsync` → typed Unsupported).
                          PtyControl = None }
            with ex ->
                disposeCreatedPipes ()
                Error(ProcessError.Spawn(command.Program, ex.Message))

    /// The honest typed refusal for `Command.Rlimit` on Windows — the ONE message both Windows spawn
    /// paths give, so the contained launch and the detached one can never drift apart on it. `Unsupported`
    /// rather than `ResourceLimit` because Windows has no per-process `setrlimit(2)` concept at ALL to
    /// apply badly: its resource caps live on the Job Object and govern the whole tree, which is a
    /// different instrument with different semantics and no honest way to stand in for a per-process one
    /// (see `ProcessGroupOptions`/`ResourceLimits` for the whole-tree caps Windows does have). Refused
    /// before any spawn work, never a child running with the caps silently dropped.
    let private rlimitUnsupportedOnWindows: ProcessError =
        ProcessError.Unsupported
            "Command.Rlimit is a Unix per-process setrlimit(2) primitive; Windows has no equivalent (use ProcessGroupOptions resource limits for whole-tree Job Object caps)"

    let spawnWindows (job: nativeint) (command: Command) : Result<Spawned, ProcessError> =
        // `Command.Umask`/`Uid`/`Gid`/`Groups`/`Setsid`/`Arg0`/`Rlimit` are Unix-only primitives with no
        // Windows equivalent (a file-mode creation mask, `setuid`/`setgid`/supplementary-group privilege
        // drop, a `setsid()` session detach, a distinct `argv[0]` — `CreateProcessW` takes one raw command
        // line, not an argv array with an independent first element — and a per-process `setrlimit(2)`
        // cap, whose nearest Windows relative is the whole-tree Job Object limit set). Honour each request honestly as
        // `ProcessError.Unsupported` BEFORE any spawn work, rather than silently ignoring it — symmetric
        // to the port's other Unix-only gates (e.g. every non-`Kill` `Signal` on Windows → `Unsupported`
        // in `Backend.fs`). Reported one at a time; the first requested-but-unsupported knob names the
        // failure.
        let config = command.Config

        if config.ExtraFds.Count > 0 then
            Error(
                ProcessError.Unsupported "Command.ExtraFd is POSIX-only; Windows has no child file-descriptor namespace"
            )
        elif not config.Rlimits.IsEmpty then
            Error rlimitUnsupportedOnWindows
        elif config.StopSignal <> Signal.Term then
            Error(
                ProcessError.Unsupported
                    $"Command.StopSignal({config.StopSignal}) on Windows; graceful stop uses the existing WM_CLOSE/CTRL+BREAK mechanisms and only the default Signal.Term contract is representable"
            )
        elif config.CancelSignal |> Option.exists (fun signal -> signal <> Signal.Term) then
            // The cancellation ladder's soft tier is the same Windows mechanism `StopSignal` gates, so it
            // is refused on the same terms and for the same reason: an arbitrary POSIX signal is not
            // representable here, and a graceful cancellation must never quietly downgrade to the hard
            // kill while the call looked like it had been honoured. Screened whether or not
            // `CancelGrace` is set, exactly like `StopSignal` (which is likewise refused with no
            // `TimeoutGrace` configured) — a knob that cannot be honoured fails loudly at the boundary,
            // rather than depending on whether some other knob happens to activate it.
            Error(
                ProcessError.Unsupported
                    $"Command.CancelSignal({CommandConfig.cancelSignal config}) on Windows; a graceful cancellation uses the existing WM_CLOSE/CTRL+BREAK mechanisms and only the default Signal.Term contract is representable"
            )
        else
            match config.Umask, config.Uid, config.Gid, config.Setsid, config.Groups, config.Arg0 with
            | Some _, _, _, _, _, _ -> Error(ProcessError.Unsupported "umask")
            | _, Some _, _, _, _, _ -> Error(ProcessError.Unsupported "uid")
            | _, _, Some _, _, _, _ -> Error(ProcessError.Unsupported "gid")
            | _, _, _, true, _, _ -> Error(ProcessError.Unsupported "setsid")
            | _, _, _, _, Some _, _ -> Error(ProcessError.Unsupported "groups")
            | _, _, _, _, _, Some _ -> Error(ProcessError.Unsupported "arg0")
            | None, None, None, false, None, None ->
                match config.Pty with
                | Some pty ->
                    // ConPTY needs Windows 10 1809+; probe the export rather than blind-calling so a pre-1809
                    // host is a typed `ProcessError.Unsupported`, never a silent pipe downgrade (D9). The spawn
                    // takes `windowsSpawnLock` for the same reason the pipe path does: a concurrent pipe spawn
                    // with `bInheritHandles = true` must not snapshot this path's inheritable pipe-client ends
                    // in its own child (this path itself passes `bInheritHandles = false`).
                    if wantsHardenedToken config then
                        // Defense in depth: the builder already refuses this pair, so a `CommandConfig` reaching
                        // here with both set could only come from inside the library. The ConPTY spawn is a
                        // different call (`CreateProcessExtended`) that does not carry the hardened token, so
                        // running it would silently give the child the parent's full token — refused instead.
                        Error(
                            ProcessError.Unsupported
                                "Pty with WindowsRestrictedToken/WindowsIntegrityLevel (the ConPTY spawn path cannot carry a hardened token)"
                        )
                    elif not (conptyAvailable ()) then
                        Error(ProcessError.Unsupported "Pty (needs Windows 10 1809+ / ConPTY)")
                    else
                        lock windowsSpawnLock (fun () -> spawnWindowsPtyCore job command pty)
                | None -> lock windowsSpawnLock (fun () -> spawnWindowsCore job command)

    // ----------------------------------------------------------------------------------
    // Windows: the detached launch (Command.LaunchDetached) — deliberately NOT contained
    // ----------------------------------------------------------------------------------
    //
    // Every other spawn in this file exists to put the child INSIDE a Job Object before it can run a
    // single instruction (CREATE_SUSPENDED -> AssignProcessToJobObject -> ResumeThread), because that Job
    // is the whole kill-on-dispose guarantee. The detached launch is the one path that deliberately does
    // the opposite: the child is created running, is assigned to NO Job at all, and no handle to it is
    // retained — so nothing the parent does (Dispose, GC, `TerminateJobObject`, even the kernel's
    // handle rundown when the parent dies) can reach it. That is the entire point of the verb; it is why
    // the verb is a loud, separate opt-out rather than a flag on the ordinary path.
    //
    // Consequences, all deliberate:
    //  * no CREATE_SUSPENDED / resume dance — nothing has to happen between creation and execution;
    //  * no `waitWindows` registration and no retained process handle — nobody observes the exit;
    //  * no pipes — a detached child has no parent-side reader (see `detachedChildHandle`).
    // What is NOT skipped: the shared PATHEXT/prefer-local/effective-child-`PATH`/cmd.exe-wrapper
    // resolution (`buildWindowsCommandLine`, T-181/T-182/T-339 — no second copy of that logic), the writable
    // command-line buffer (T-198), the environment block, and `windowsSpawnLock` — this path passes
    // `bInheritHandles = true`, so it must not run concurrently with another spawn whose inheritable
    // pipe ends would otherwise be snapshotted into this child (which would keep that run's reads from
    // ever seeing EOF).

    /// One std handle for a detached child: a `Command.StdoutToFile`/`StderrToFile` redirect, an
    /// inherited copy of the parent's own std handle (`StdioMode.Inherit`), or the null device.
    /// `StdioMode.Piped` — the builder default — resolves to the null device here rather than a pipe:
    /// a detached child has no parent left to drain one, and a pipe whose read end is closed would give
    /// the child `ERROR_BROKEN_PIPE` on its first write. That is the documented contract of the detached
    /// verb (keep output with `StdoutToFile`, or share the caller's console with `StdioMode.Inherit`),
    /// not a per-run downgrade; every knob that genuinely needs a parent-side reader (tees, line
    /// handlers, the capture verbs) is refused up front by the verb layer instead.
    /// Failure is raised, not returned, so the caller's single `with` turns it into `ProcessError.Spawn`
    /// — the same shape `spawnWindowsCore`'s `setupOut` uses.
    let private detachedChildHandle
        (label: string)
        (fileRedirect: (string * bool) option)
        (mode: StdioMode)
        (stdHandleId: int)
        (nulAccess: uint32)
        : nativeint =
        let handle =
            match fileRedirect with
            | Some(path, append) -> inheritableFile path append
            | None ->
                match mode with
                | StdioMode.Inherit -> inheritableStdHandle stdHandleId
                | StdioMode.Null
                | StdioMode.Piped -> inheritableNul nulAccess

        if not (isValidHandle handle) then
            let message = Win32Exception(Marshal.GetLastWin32Error()).Message
            failwith $"could not open the detached child's {label} handle: {message}"

        handle

    /// Launch `command` as a detached child — running, contained by nothing, unowned (see the section
    /// comment above). Returns its pid and the OS-reported start time, read while our own process handle
    /// is still open so the pid cannot have been recycled underneath the identity pair. The `Umask`/
    /// `Uid`/`Gid`/`Groups`/`Setsid`/`Arg0`/`Rlimit` Unix-only knobs are refused exactly as `spawnWindows`
    /// refuses them, so the detached path diverges from the ordinary one only where detachment itself
    /// requires it.
    let spawnDetachedWindows (command: Command) : Result<DetachedSpawn, ProcessError> =
        let config = command.Config

        match
            config.Umask, config.Uid, config.Gid, config.Setsid, config.Groups, config.Arg0, config.Rlimits.IsEmpty
        with
        | Some _, _, _, _, _, _, _ -> Error(ProcessError.Unsupported "umask")
        | _, Some _, _, _, _, _, _ -> Error(ProcessError.Unsupported "uid")
        | _, _, Some _, _, _, _, _ -> Error(ProcessError.Unsupported "gid")
        | _, _, _, true, _, _, _ -> Error(ProcessError.Unsupported "setsid")
        | _, _, _, _, Some _, _, _ -> Error(ProcessError.Unsupported "groups")
        | _, _, _, _, _, Some _, _ -> Error(ProcessError.Unsupported "arg0")
        | _, _, _, _, _, _, false -> Error rlimitUnsupportedOnWindows
        | None, None, None, false, None, None, true ->
            // Decide the launch (PATHEXT / effective-child-`PATH` substitution / cmd.exe batch wrapper)
            // BEFORE any handle is allocated, exactly as `spawnWindowsCore` does — an unsafe batch
            // argument, or a program the command's own overridden child `PATH` does not hold, is refused
            // here. A detached launch is fire-and-forget, so refusing a wrong-`PATH` namesake up front is
            // the only chance to refuse it at all.
            match buildWindowsCommandLine command with
            | Error error -> Error error
            | Ok commandLine ->
                // Every inheritable handle opened for the child, dropped once the child has its own
                // inherited copies (or immediately, if the launch failed or threw partway through).
                let parentCopies = ResizeArray<nativeint>()

                let releaseParentCopies () =
                    for handle in parentCopies do
                        closeHandleIfValid handle

                    // Emptied as they are closed, so the unwind path below can never close one twice: a
                    // Win32 handle VALUE is recycled the moment it is freed, so a second `CloseHandle` on
                    // it could hit an unrelated object this process has since opened.
                    parentCopies.Clear()

                lock windowsSpawnLock (fun () ->
                    try
                        let stdinChild =
                            let mode =
                                if Stdin.isInherit config.StdinSource then
                                    StdioMode.Inherit
                                else
                                    // The verb layer has already refused every feeder source (there is no
                                    // parent to pump one), so the only remaining stdin is an immediate EOF.
                                    StdioMode.Null

                            let handle = detachedChildHandle "stdin" None mode STD_INPUT_HANDLE GENERIC_READ
                            parentCopies.Add handle
                            handle

                        let outChild =
                            let handle =
                                detachedChildHandle
                                    "stdout"
                                    config.StdoutFile
                                    config.StdoutMode
                                    STD_OUTPUT_HANDLE
                                    GENERIC_WRITE

                            parentCopies.Add handle
                            handle

                        let errChild =
                            if config.MergeStderr then
                                // `2>&1` at the OS level: stderr shares the SAME handle as stdout, so both
                                // land in the one destination and interleave honestly. Not added to
                                // `parentCopies` a second time — that would be a double `CloseHandle`.
                                outChild
                            else
                                let handle =
                                    detachedChildHandle
                                        "stderr"
                                        config.StderrFile
                                        config.StderrMode
                                        STD_ERROR_HANDLE
                                        GENERIC_WRITE

                                parentCopies.Add handle
                                handle

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
                            // Deliberately NO `CREATE_SUSPENDED`: there is no Job to assign the child to
                            // before it runs, so there is nothing to suspend it for. Deliberately NO
                            // `CREATE_BREAKAWAY_FROM_JOB` either: this verb opts out of the containment
                            // ProcessKit creates, and cannot opt out of a Job the CALLER's own process was
                            // placed in by someone else (the kernel puts a child of a job-bound process in
                            // that same job). Requesting breakaway would fail outright with
                            // ERROR_ACCESS_DENIED on every ambient job lacking `JOB_OBJECT_LIMIT_BREAKAWAY_OK`
                            // — turning a working launch into a spawn failure — so the ambient case is
                            // documented (docs/commands.md, docs/platform-support.md) rather than fought.
                            (if environment = IntPtr.Zero then
                                 0u
                             else
                                 CREATE_UNICODE_ENVIRONMENT)
                            ||| (if config.CreateNoWindow then CREATE_NO_WINDOW else 0u)
                            // Honoured for its OS-level effect — the child becomes the root of its own
                            // console process group, so a CTRL+C/CTRL+BREAK in the caller's console is not
                            // delivered to it. ProcessKit itself offers no way to signal a detached child
                            // (that would need the containment this verb opted out of).
                            ||| (if config.WindowsCtrlSignals then
                                     CREATE_NEW_PROCESS_GROUP
                                 else
                                     0u)
                            ||| (match config.Priority with
                                 | Some priority -> PriorityMapping.windowsCreationFlag priority
                                 | None -> 0u)

                        // A PRIVATE, writable copy of the command line: `CreateProcessW` may patch this
                        // buffer in place, so it must never be the memory of a managed string (T-198).
                        let commandLineBuffer = Marshal.StringToHGlobalUni commandLine

                        // The same token-aware seam the contained path uses: a detached launch opts out of
                        // containment, not of the hardening the caller asked for.
                        let creation =
                            createChildProcess
                                config
                                commandLineBuffer
                                flags
                                environment
                                workingDirectory
                                &startup
                                &info

                        let struct (created, lastError) =
                            match creation with
                            | Created(succeeded, error) -> struct (succeeded, error)
                            | TokenFailed _ -> struct (false, 0)

                        Marshal.FreeHGlobal commandLineBuffer

                        if environment <> IntPtr.Zero then
                            Marshal.FreeHGlobal environment

                        if not created then
                            releaseParentCopies ()

                            match creation with
                            | TokenFailed reason -> Error(ProcessError.Spawn(command.Program, reason))
                            | Created _ ->
                                if lastError = ERROR_FILE_NOT_FOUND || lastError = ERROR_PATH_NOT_FOUND then
                                    Error(notFoundFromSpawnFailure command)
                                elif wantsHardenedToken config then
                                    Error(hardenedSpawnError command lastError)
                                else
                                    Error(ProcessError.Spawn(command.Program, Win32Exception(lastError).Message))
                        else
                            let pid = int info.dwProcessId

                            // Read the identity pair while `info.hProcess` is STILL OPEN: Windows does not
                            // recycle a pid while any handle to that process remains, so the start time we
                            // pair with the pid cannot already belong to a different process. After the
                            // closes below nothing pins this pid any more — by design.
                            let startTime = readProcessStartTime pid

                            CloseHandle info.hThread |> ignore
                            CloseHandle info.hProcess |> ignore
                            // The child holds its own inherited copies now; drop ours so a redirect file or
                            // the null device is not kept open by this process for the child's lifetime.
                            releaseParentCopies ()

                            Ok { Pid = pid; StartTime = startTime }
                    with ex ->
                        releaseParentCopies ()
                        Error(ProcessError.Spawn(command.Program, ex.Message)))
