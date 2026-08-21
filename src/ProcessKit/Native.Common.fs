namespace ProcessKit.Native

open ProcessKit
open System
open System.ComponentModel
open System.IO
open System.Runtime.InteropServices
open System.Threading.Tasks

/// Shared, platform-neutral pieces of the low-level native layer.
///
/// Internal: the public surface is `ProcessGroup` / `JobRunner`. The single `Native.fs` was split
/// into platform-scoped modules — `Native.Common` (here), `Native.Windows`, `Native.Posix`, and
/// `Native.Cgroup` — because F# does not allow one `module` to span files. This module holds the
/// types and helpers the platform layers all depend on, so it compiles first: the `Spawned` result
/// shared by `spawnWindows`/`spawnPosix`, the `SignalDelivery` classification used by the POSIX
/// signal + cgroup layers, and the shared environment builder.
module internal Common =

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
            /// Parent sides of POSIX-only full-duplex channels, keyed by the child fd each peer was
            /// dup2'd onto. Empty on Windows and when no Command.ExtraFd was configured.
            ExtraFds: (int * Stream) list
            /// Windows only: `true` when the child opted into registration as a targetable console process
            /// group, so `ProcessGroup.Signal(Signal.Int/Term)` can deliver a best-effort
            /// `GenerateConsoleCtrlEvent(CTRL_BREAK_EVENT, pid)` to it. This is distinct from merely being
            /// created with `CREATE_NEW_PROCESS_GROUP`: every ConPTY child is isolated that way, while a
            /// default ConPTY child remains unregistered here. Always `false` on POSIX, which delivers
            /// signals through `killpg` regardless of this flag.
            WindowsCtrlGroup: bool
            /// The retained PTY control handle/fd for a `Command.Pty` run, kept for the child's whole
            /// lifetime so `RunningProcess.ResizeAsync` can reach it — the Windows pseudoconsole handle
            /// (`HPCON`, the resize target for `ResizePseudoConsole`) or the POSIX pty MASTER fd (the
            /// `ioctl(TIOCSWINSZ)` target). `None` for every non-PTY spawn, on which `ResizeAsync`
            /// returns a typed `ProcessError.Unsupported` rather than a silent no-op (D6).
            PtyControl: nativeint option
        }

    /// A freshly spawned **detached** child (`Command.LaunchDetached`) — the deliberate opt-out from
    /// containment. Deliberately unlike `Spawned`: there is no handle to hold, no stream to pump and no
    /// PTY control. The public detached descriptor owns no wait, signal, kill, or other lifetime control;
    /// on POSIX, a private reaper owns the exit-status wait after the identity snapshot is captured. Only
    /// the two identity facts the platform layer can capture while the pid is still pinned (Windows: our
    /// own open process handle; POSIX: the direct child before handoff) survive the launch, and the verb
    /// layer turns them into the public `DetachedProcess`.
    type DetachedSpawn =
        {
            /// The OS process id of the detached child.
            Pid: int
            /// Its OS-reported start time (see `readProcessStartTime`), the pid-reuse disambiguator, or
            /// `None` when the platform could not report one — never fabricated.
            StartTime: DateTime option
        }

    /// A parent-side stdin stream whose LOGICAL end of input is more than closing the handle, so the
    /// ordinary teardown-race-safe dispose would leave the child waiting for an EOF that never comes.
    ///
    /// Both implementations today are PTY stdin views, and both are NON-owning: the POSIX one
    /// (`Native.Posix.PtyStdinStream`) is a second view over the pty master fd the merged-output stream owns,
    /// and the Windows one (`Native.Windows.ConPtyStdinStream`) writes through the ConPTY host-input pipe the
    /// pseudoconsole session itself keeps open. Closing either releases nothing and the child's terminal
    /// never goes away under it — the child sees end of input only once its terminal receives that terminal's
    /// own end-of-input gesture. Every stdin stream that is simply a pipe end (the socketpair paths, a
    /// non-PTY Windows spawn) does NOT implement this, and keeps being closed by dispose.
    ///
    /// Both reliable finish paths route through this when the stream implements it — `ProcessStdin.FinishAsync`
    /// (the interactive handle) and the bulk stdin feeder (`Pump.feedStdin*`, when the source is the child's
    /// complete input) — and fall back to `Pump.disposeQuietly`/`disposeQuietlyAsync` when it does not.
    type IStdinFinisher =
        /// Deliver end of input to the child, WITHOUT releasing a handle this stream does not own.
        ///
        /// Idempotent: the first call performs the delivery, later ones are no-ops (`ProcessStdin.FinishAsync`
        /// and `PtySession.CloseStdinAsync` both promise that). Writes through the stream are refused once it
        /// has been finished, rather than silently trailing input past the end of input the child has seen.
        ///
        /// A genuine delivery failure faults the returned task instead of being swallowed — a child reading to
        /// EOF would otherwise hang forever on a silently dropped end of input. The two cases where end of
        /// input is moot rather than failed — the child has already closed its terminal, or the run's own
        /// teardown already released this stream — complete successfully.
        abstract member FinishAsync: unit -> Task

    /// The OS-reported start time of `pid` (`System.Diagnostics.Process.StartTime`, local kind), or
    /// `None` when the process has exited between enumeration and this read, or its start time is
    /// inaccessible on this platform/timing. The single cross-platform start-time read shared by the
    /// enriched member snapshot on every platform (`ProcessGroup.MembersInfo`, via `Native.Windows`/
    /// `Native.Posix`). Never throws; never reads the process's command line or environment.
    let readProcessStartTime (pid: int) : DateTime option =
        try
            use proc = System.Diagnostics.Process.GetProcessById pid
            Some proc.StartTime
        with _ ->
            // The process exited between enumeration and this read, or its start time is inaccessible on
            // this platform/timing (a protected/system process, or an unsupported platform) — honestly
            // `None` rather than a fabricated timestamp. `GetProcessById` throws `ArgumentException` for a
            // dead pid and can throw `InvalidOperationException`/`Win32Exception` for `StartTime`; none is
            // recoverable here and each means the same "no readable start time".
            None

    /// The effective environment for the child: the inherited set (unless cleared) with the
    /// command's overrides applied (`Some` sets, `None` removes).
    let effectiveEnvironment (command: Command) =
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

    // errno for "no such process" — Linux and macOS agree on the value. `killpg`/`kill` return this
    // when the target (process, process group, or single pid) no longer exists: a race with the
    // target's own exit, not a caller error, so it is classified as a best-effort success below.
    [<Literal>]
    let private ESRCH = 3

    /// The result of one `killpg`/`kill` signal-delivery attempt, distinguishing "the target already
    /// exited" (best-effort success — a race with process exit, not a caller error) from a genuine
    /// delivery failure (any other non-zero errno, e.g. EINVAL for an invalid signal number).
    [<RequireQualifiedAccess; NoComparison; NoEquality>]
    type SignalDelivery =
        /// The kernel accepted the signal.
        | Delivered
        /// errno ESRCH: the target no longer exists.
        | TargetGone
        /// Any other non-zero errno — the call itself failed.
        | DeliveryFailed of Errno: int * Message: string

    /// Classify the return of a `killpg`/`kill` call. `Marshal.GetLastWin32Error()` must be read here,
    /// immediately after the native call that produced `returnCode` — before any other P/Invoke — since
    /// it is a per-thread value the runtime only guarantees valid until the next `SetLastError`d call.
    let classifySignalDelivery (returnCode: int) : SignalDelivery =
        if returnCode = 0 then
            SignalDelivery.Delivered
        else
            let errno = Marshal.GetLastWin32Error()

            if errno = ESRCH then
                SignalDelivery.TargetGone
            else
                SignalDelivery.DeliveryFailed(errno, Win32Exception(errno).Message)

    // ---------------------------------------------------------------------------
    // PATH/PATHEXT resolution — one shared implementation backs BOTH the no-spawn preflight
    // (`Exec.which` / `CliClient.EnsureAvailableAsync` / `Command.ResolveProgram`) and the spawn path's
    // own `ProcessError.NotFound` diagnostic enrichment (populating `Searched` after the OS itself
    // reports a missing program), so the two can never disagree on "found vs not found" for the same
    // program name. It also decides the real Windows launch (`resolveWindowsLaunch`): wherever the OS's
    // own search cannot reach the match this resolution found — a `PATHEXT` shim, a prefer-local
    // directory, or a child `PATH` the command overrode, none of which `CreateProcessW` searches — the
    // resolved absolute path is substituted into the launch instead of the bare name, and a miss the OS
    // would answer out of a DIFFERENT `PATH` is refused before the spawn. Everywhere else the launch is
    // still left to the OS's own resolution (`CreateProcessW`/`posix_spawnp`).
    // ---------------------------------------------------------------------------

    /// Whether `program` is a bare name — a single path segment with no directory separator — that
    /// should be looked up via `PATH`. A path-form program (`"./tool"`, `"C:\tools\tool.exe"`,
    /// `"/usr/bin/tool"`) returns `false`: it is resolved directly (see `resolveProgram`), with no
    /// `PATH` search, exactly like the OS itself resolves it. `/` is a separator on every platform;
    /// `\` is a separator only on Windows (an ordinary filename character on POSIX).
    let isBareName (program: string) : bool =
        if String.IsNullOrEmpty program then
            false
        else
            program.IndexOf '/' < 0
            && (not (RuntimeInformation.IsOSPlatform OSPlatform.Windows)
                || program.IndexOf '\\' < 0)

    /// The default `PATHEXT` used when the environment variable itself is unset/empty — the same
    /// fallback `cmd.exe` falls back to.
    [<Literal>]
    let private defaultPathExt = ".COM;.EXE;.BAT;.CMD"

    /// Check whether `program` exists as a directly-executable file in `dir`, returning its full path.
    /// Windows: `PATHEXT`-aware, driven by the explicit `pathExt` source (`""` → the `cmd.exe` default
    /// set) rather than reading the environment itself — the caller passes the effective PATHEXT (the
    /// current process's, or a command's child override), so one probe serves both. The bare name is
    /// accepted as-is only when it already carries a recognized executable extension (`git.exe`,
    /// `git.cmd`, …); otherwise each `PATHEXT` extension is tried in order, appended to the bare name.
    /// POSIX: `pathExt` is ignored (there is no PATHEXT); the plain file must exist and carry at least
    /// one executable permission bit.
    ///
    /// Exception-safe by construction: this candidate directory can vanish, or a matching file's
    /// permissions can become unreadable, between the `File.Exists` existence check and the follow-up
    /// probe (on POSIX, `File.GetUnixFileMode` — a genuine TOCTOU window, since another process/thread
    /// can delete or replace the file in between). Any such race or access failure on THIS candidate is
    /// caught and treated as "this candidate didn't pan out" (`None`), never a raw exception — so
    /// `findInPath`'s PATH walk simply continues to the next directory instead of aborting the whole
    /// resolution.
    let probeDir (pathExt: string) (dir: string) (program: string) : string option =
        try
            if RuntimeInformation.IsOSPlatform OSPlatform.Windows then
                let effectivePathExt =
                    if String.IsNullOrEmpty pathExt then
                        defaultPathExt
                    else
                        pathExt

                let extensions =
                    effectivePathExt.Split ';'
                    |> Array.map (fun e -> e.Trim())
                    |> Array.filter (fun e -> e <> "")

                let carriesExecExt (path: string) =
                    let ext = Path.GetExtension path

                    not (String.IsNullOrEmpty ext)
                    && extensions
                       |> Array.exists (fun candidate ->
                           String.Equals(candidate, ext, StringComparison.OrdinalIgnoreCase))

                let candidate = Path.Combine(dir, program)

                if carriesExecExt candidate && File.Exists candidate then
                    Some candidate
                else
                    extensions
                    |> Array.tryPick (fun ext ->
                        let named = candidate + ext
                        if File.Exists named then Some named else None)
            else
                let candidate = Path.Combine(dir, program)

                if File.Exists candidate then
                    let executableBits =
                        UnixFileMode.UserExecute
                        ||| UnixFileMode.GroupExecute
                        ||| UnixFileMode.OtherExecute

                    if (File.GetUnixFileMode candidate &&& executableBits) <> UnixFileMode.None then
                        Some candidate
                    else
                        None
                else
                    None
        with
        | :? IOException
        | :? UnauthorizedAccessException ->
            // The candidate vanished (TOCTOU race, e.g. `File.GetUnixFileMode` raising
            // `FileNotFoundException` after `File.Exists` already returned `true`) or is otherwise
            // inaccessible (exotic filesystem/permissions). Neither is a caller error — it just means
            // this one candidate does not resolve; the PATH walk continues to the next directory exactly
            // as it would for a plain "not present" candidate.
            None

    /// The effective lookup inputs a resolution reads INSTEAD of the current process environment: the
    /// `PATH` to walk for a bare name, the `PATHEXT` governing Windows extension probing, and the
    /// prefer-local directories to search first (each already anchored — a relative one against the
    /// command's working directory). Built either from the current process environment (`processContext`,
    /// backing `Exec.which`/`CliClient.EnsureAvailableAsync`) or from a command's EFFECTIVE child
    /// environment + prefer-local (`commandContext`, backing
    /// `Command.ResolveProgram`/`CliClient.ResolveProgram` and the spawn's own `NotFound` diagnostic).
    /// Neither context ever resolves the POSIX security helpers (`setpriv`/`setsid`): those are pinned to
    /// a fixed trusted directory list by `Native.Posix.trustedHelperPath`, which reuses `probeDir` alone.
    /// Threading this one value through the SAME resolver (`resolveWith`) is what lets preflight, the real
    /// launch substitution (`resolveWindowsLaunch`/`resolvePreferLocal`), and the not-found diagnostic all
    /// agree without a second copy of the PATH/PATHEXT/exec-bit rules. `Path`/`PathExt` are `""` when
    /// unset (the resolver then treats `PATH` as empty and `PATHEXT` as the `cmd.exe` default set).
    [<NoComparison; NoEquality>]
    type ResolveContext =
        {
            Path: string
            PathExt: string
            PreferLocal: string list
            WorkingDirectory: string option
            /// `true` only when the command inherits a non-empty CURRENT PROCESS `PATH` unchanged — the
            /// value an OS bare-name search walks before the child exists. While this is `true` the native
            /// search and this resolution can only find the same PATH match, so Windows and POSIX can safely
            /// defer to it. An explicit clear/remove/override, or an absent/empty process `PATH`, turns it
            /// `false`; the launch resolver then substitutes the resolved absolute path (or refuses a miss)
            /// instead of exposing libc default/current-directory fallback semantics that the resolver does
            /// not share.
            PathIsProcessPath: bool
        }

    /// The current process's own `PATH` (`""` when unset) — the value Windows' own bare-name search
    /// walks, and the baseline a command's effective child `PATH` is compared against.
    let private processSearchPath () =
        match Environment.GetEnvironmentVariable "PATH" with
        | null -> ""
        | value -> value

    /// The resolution context of the CURRENT PROCESS: its own `PATH`/`PATHEXT`, and NO prefer-local
    /// directories. This is the historical `Exec.which`/`resolveProgram` behaviour — resolve a program
    /// name exactly as the current process itself would — kept byte-for-byte so `Exec.which`'s semantics
    /// are unchanged (it answers "is this tool installed on the host", against the process's own `PATH`).
    let processContext () : ResolveContext =
        let read name =
            match Environment.GetEnvironmentVariable name with
            | null -> ""
            | value -> value

        { Path = read "PATH"
          PathExt = read "PATHEXT"
          PreferLocal = []
          WorkingDirectory = None
          PathIsProcessPath = true }

    /// The resolution context of `command`'s EFFECTIVE CHILD environment: the `PATH`/`PATHEXT` the child
    /// will actually see (its `Env`/`EnvRemove`/`EnvClear` applied to the inherited set — reusing the very
    /// `effectiveEnvironment` builder the spawn hands the child, so the case-insensitive `PATH`/`PATHEXT`
    /// lookup on Windows and the override/removal semantics are identical to the launch), plus its
    /// `PreferLocal` directories anchored to `CurrentDir` (a relative one against the working directory,
    /// else left as-is for the process cwd), consulted before `PATH`. So a preflight resolution and the
    /// spawn's own diagnostic reflect the `PATH` the child launches against — not the parent process's.
    let commandContext (command: Command) : ResolveContext =
        let env = effectiveEnvironment command

        let lookup name =
            match env.TryGetValue name with
            | true, value -> value
            | _ -> ""

        let path = lookup "PATH"
        let baseDir = command.Config.WorkingDirectory

        let preferLocal =
            command.Config.PreferLocal
            |> Seq.map (fun dir ->
                if Path.IsPathRooted dir then
                    dir
                else
                    match baseDir with
                    | Some cwd -> Path.Combine(cwd, dir)
                    | None -> dir)
            |> List.ofSeq

        let pathComparison =
            if RuntimeInformation.IsOSPlatform OSPlatform.Windows then
                StringComparison.OrdinalIgnoreCase
            else
                StringComparison.Ordinal

        let explicitlyChangesPath =
            command.Config.ClearEnv
            || (command.Config.EnvOverrides
                |> Seq.exists (fun (key, _) -> String.Equals(key, "PATH", pathComparison)))

        let processPath = processSearchPath ()

        { Path = path
          PathExt = lookup "PATHEXT"
          PreferLocal = preferLocal
          WorkingDirectory = baseDir
          // Equality alone is insufficient proof: an explicit child-PATH operation can produce the same
          // managed string, and libc assigns absent/empty PATH searches default/current-directory semantics
          // that this resolver deliberately does not. Defer only for an untouched, non-empty inherited PATH.
          PathIsProcessPath =
            not explicitlyChangesPath
            && not (String.IsNullOrEmpty processPath)
            && String.Equals(path, processPath, StringComparison.Ordinal) }

    [<DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "NeedCurrentDirectoryForExePathW")>]
    extern bool private needCurrentDirectoryForExePath(string fileName)

    /// Walk the directories that `CreateProcessW` with `lpApplicationName = NULL` searches for `program`
    /// (a bare name — see `isBareName`), followed by `ctx.Path`, reusing `probeDir` (with the context's
    /// `PathExt`) for each directory in order. On Windows this includes the loaded application's directory,
    /// the parent process's current directory when `NeedCurrentDirectoryForExePathW` includes it, the system
    /// directories, and the Windows directory before `PATH`; the process's current directory is deliberately
    /// used rather than `ctx.WorkingDirectory`, because `lpCurrentDirectory` only sets the child's directory
    /// after executable resolution. Relative `PATH` entries are anchored to the effective working directory
    /// before probing. On POSIX, each empty component of a non-empty `PATH` is the effective working directory
    /// at that exact search position; Windows continues to ignore empty `PATH` entries. Every hit is normalized
    /// to a full path. Returns `(found, searched)`: `found` is the first matching directory's resolved path;
    /// `searched` is the raw `PATH` value, for the `NotFound` diagnostic (`""` when the `PATH` is unset/empty).
    ///
    /// `PATH` is split on the raw `Path.PathSeparator` with NO quote handling, on Windows as on POSIX —
    /// deliberate and verified. Positional empty components are preserved only on POSIX, after the non-empty
    /// `PATH` guard above distinguishes them from the wholly absent/empty value. The real Windows bare-name
    /// launch is `CreateProcessW` with
    /// `lpApplicationName = NULL` (see `spawnWindowsCore`), which lets the OS resolve the program, and
    /// neither that search nor `SearchPathW` strips surrounding double quotes from a `PATH` entry: a
    /// quoted directory (`"C:\Program Files\Foo"`) is a literal, non-existent path to them, so a program
    /// inside it is unreachable to the actual spawn. Stripping quotes HERE would make this shared
    /// preflight/diagnostic resolver report `found` for a program the real spawn can never launch — the
    /// exact `which`-vs-spawn divergence this resolver exists to prevent, merely inverted. So mirror the
    /// OS: a quoted entry is treated as the (invalid) directory name it literally is and simply matches
    /// nothing, exactly as the spawn finds nothing.
    let private findInContextPath (ctx: ResolveContext) (program: string) : string option * string =
        let effectiveWorkingDirectory =
            match ctx.WorkingDirectory with
            | Some directory -> Path.GetFullPath directory
            | None -> Directory.GetCurrentDirectory()

        let resolvePathEntry (directory: string) =
            if String.IsNullOrEmpty directory then
                effectiveWorkingDirectory
            elif Path.IsPathRooted directory then
                directory
            else
                Path.Combine(effectiveWorkingDirectory, directory)

        let pathDirs =
            if String.IsNullOrEmpty ctx.Path then
                [||]
            else
                let entries = ctx.Path.Split Path.PathSeparator

                let entries =
                    if RuntimeInformation.IsOSPlatform OSPlatform.Windows then
                        entries |> Array.filter (fun directory -> directory <> "")
                    else
                        entries

                entries |> Array.map resolvePathEntry

        let dirs =
            if RuntimeInformation.IsOSPlatform OSPlatform.Windows then
                let applicationDirectory =
                    match Environment.ProcessPath with
                    | null
                    | "" -> []
                    | processPath ->
                        match Path.GetDirectoryName processPath with
                        | null
                        | "" -> []
                        | directory -> [ directory ]

                let windowsDirectory = Environment.GetFolderPath Environment.SpecialFolder.Windows
                let systemDirectory = Environment.SystemDirectory

                let legacySystemDirectory =
                    if String.IsNullOrEmpty windowsDirectory then
                        []
                    else
                        [ Path.Combine(windowsDirectory, "System") ]

                let currentDirectory =
                    if needCurrentDirectoryForExePath program then
                        [ Directory.GetCurrentDirectory() ]
                    else
                        []

                applicationDirectory
                @ currentDirectory
                @ [ systemDirectory ]
                @ legacySystemDirectory
                @ [ windowsDirectory ]
                @ Array.toList pathDirs
                |> List.filter (fun directory -> not (String.IsNullOrEmpty directory))
                |> List.toArray
            else
                pathDirs

        (dirs
         |> Array.tryPick (fun dir -> probeDir ctx.PathExt dir program |> Option.map Path.GetFullPath)),
        ctx.Path

    /// Resolve `program` (a bare name) against `ctx.PreferLocal` — the prefer-local directories, already
    /// anchored — searched in the order they were added, BEFORE any `PATH` lookup (T-182). Each directory
    /// is probed with the SAME `probeDir` the `PATH` walk uses (PATHEXT on Windows, the executable bit on
    /// POSIX), so preflight and launch can never disagree on what a directory contains. The first match is
    /// returned as an **absolute** path (`Path.GetFullPath`): the OS never searches these directories
    /// itself, so a prefer-local hit is substituted into the launch as a full path, whatever its
    /// extension. `None` when there are no prefer-local directories or none of them holds the program.
    let private findPreferLocal (ctx: ResolveContext) (program: string) : string option =
        if List.isEmpty ctx.PreferLocal then
            None
        else
            ctx.PreferLocal
            |> List.tryPick (fun dir -> probeDir ctx.PathExt dir program |> Option.map Path.GetFullPath)

    /// The `NotFound` a **bare-name** lookup produces when neither prefer-local nor the context's `PATH`
    /// holds the program: `Searched` names the raw `PATH` value that was walked, and is omitted when there
    /// was none to walk. One helper, so the preflight/diagnostic resolution (`resolveWith`) and the
    /// Windows launch's own pre-spawn refusal (`resolveWindowsLaunch`) report the IDENTICAL error for the
    /// same command config instead of two hand-built copies that can drift apart.
    let private bareNameNotFound (program: string) (searched: string) : ProcessError =
        ProcessError.NotFound(program, (if searched = "" then None else Some searched))

    /// The single resolution both preflight (`Exec.which`/`Command.ResolveProgram`/`CliClient.*`) and the
    /// spawn path's own `NotFound` enrichment (`notFoundFromSpawnFailure`) — plus the Windows launch
    /// substitution (`resolveWindowsLaunch`) and the POSIX prefer-local substitution (`resolvePreferLocal`)
    /// — go through, against an explicit `ctx` (the current process's environment, or a command's effective
    /// child environment + prefer-local). A bare name is looked up prefer-local first (T-182), then
    /// `ctx.Path` (typed `ProcessError.NotFound` with `Searched` naming the probed `PATH` value on a miss);
    /// a path-form program is checked directly against its own directory component with the SAME `probeDir`
    /// (so a missing extension is still resolved on Windows), anchored to the command working directory
    /// when present, never against `PATH` or prefer-local, and its `NotFound` carries no `Searched`.
    ///
    /// Never throws: `probeDir` already absorbs per-candidate IO/access races (see its own doc comment),
    /// so a raw exception surfacing here would be something unexpected at the level of the whole resolution
    /// (not tied to one candidate) — the outer `with` below is that last-resort net, turning it into a
    /// typed `ProcessError.Io` instead of letting it escape the `Result`-returning contract callers promise.
    let resolveWith (ctx: ResolveContext) (program: string) : Result<string, ProcessError> =
        try
            if String.IsNullOrWhiteSpace program then
                Error(ProcessError.NotFound(program, None))
            elif isBareName program then
                match findPreferLocal ctx program with
                | Some found -> Ok found
                | None ->
                    match findInContextPath ctx program with
                    | Some found, _ -> Ok found
                    | None, searched -> Error(bareNameNotFound program searched)
            else
                let programDirectory =
                    match Path.GetDirectoryName program with
                    | null
                    | "" -> "."
                    | d -> d

                let directory =
                    if Path.IsPathRooted program then
                        programDirectory
                    else
                        match ctx.WorkingDirectory with
                        | Some cwd -> Path.Combine(cwd, programDirectory)
                        | None -> programDirectory

                let fileName =
                    match Path.GetFileName program with
                    | null -> program
                    | name -> name

                match probeDir ctx.PathExt directory fileName with
                | Some found -> Ok(Path.GetFullPath found)
                | None -> Error(ProcessError.NotFound(program, None))
        with ex ->
            Error(ProcessError.Io $"failed to resolve '{program}': {ex.Message}")

    /// Resolve `program` against the CURRENT PROCESS's `PATH`/`PATHEXT` (and no prefer-local) — a
    /// program-name preflight against the parent's own environment. Backs `Exec.which`/
    /// `CliClient.EnsureAvailableAsync`, unchanged from before this became one case of `resolveWith`.
    /// Deliberately NOT used for the POSIX `setpriv`/`setsid` security helpers: a `PATH` lookup is what
    /// `Native.Posix.trustedHelperPath` exists to avoid for those.
    let resolveProgram (program: string) : Result<string, ProcessError> = resolveWith (processContext ()) program

    /// Resolve `command`'s program against its EFFECTIVE CHILD environment (`commandContext`): the child's
    /// `PATH`/`PATHEXT` (its `Env`/`EnvRemove`/`EnvClear` applied) with its `PreferLocal` directories
    /// consulted first — the `PATH` the child will actually launch against, not the parent process's. Backs
    /// `Command.ResolveProgram`/`CliClient.ResolveProgram` and the spawn's own `NotFound` diagnostic, so a
    /// preflight resolve and a real spawn of the SAME command config can never disagree on
    /// found-vs-not-found or on the `Searched` diagnostic.
    let resolveCommandProgram (command: Command) : Result<string, ProcessError> =
        resolveWith (commandContext command) command.Program

    /// Enrich a spawn-time not-found failure with the `Searched` diagnostic, reusing `resolveCommandProgram`
    /// (the command's EFFECTIVE child `PATH`/prefer-local) so the spawn path and a `Command.ResolveProgram`
    /// preflight of the SAME config can never disagree. The OS itself already reported the program as not
    /// found; if this redo unexpectedly resolves it anyway, that is an honest `ProcessError.Spawn`, never a
    /// false `NotFound` — two known ways this can happen: a matching file exists but is not directly
    /// executable (a permissions issue), or (Windows only) the full `PATHEXT` search matched a `.bat`/`.cmd`
    /// sibling that raw `CreateProcess` itself can never launch without a shell (it only auto-appends
    /// `.exe`) — a real, if narrow, `which`-vs-spawn gap this reports honestly rather than silently.
    let notFoundFromSpawnFailure (command: Command) : ProcessError =
        match resolveCommandProgram command with
        | Ok resolved ->
            ProcessError.Spawn(
                command.Program,
                $"the OS reported the program as not found, but it resolves locally to '{resolved}' — check that it is directly executable (a .bat/.cmd match needs a shell to run; otherwise check its executable permissions)"
            )
        | Error error -> error

    /// Resolve `command`'s program against its prefer-local directories (`Command.PreferLocal`), searched
    /// in the order they were added, BEFORE any `PATH` lookup (T-182). Only a **bare name** is resolved
    /// this way — a path-form program is handed to the OS verbatim (exactly as `resolveProgram`/the OS
    /// resolve it), so prefer-local never applies to one, mirroring how `PATH` never applies either. Routes
    /// through the SAME `commandContext`/`findPreferLocal` the preflight and the Windows launch use (no
    /// second copy), so a relative directory anchors to the command's `CurrentDir` when set (otherwise the
    /// process's current directory) and the first match is returned as an **absolute** path. `None` when
    /// there are no prefer-local directories, the program is path-form, or none of them holds the program —
    /// the caller then falls back to the ordinary `PATH` launch.
    let resolvePreferLocal (command: Command) : string option =
        let program = command.Program

        if command.Config.PreferLocal.IsEmpty || not (isBareName program) then
            None
        else
            findPreferLocal (commandContext command) program

    /// How a POSIX spawn should launch `program` after prefer-local substitution has had its say. libc's
    /// `posix_spawnp` searches the invoking process's `PATH`, not the `envp` block supplied for the child,
    /// so deferring a bare name is safe only while the effective child `PATH` is byte-for-byte identical to
    /// that process path. A command that changes or clears it is resolved here through the same
    /// `commandContext` used by `Command.ResolveProgram`; a hit is pinned by absolute path and a miss is the
    /// identical pre-spawn `ProcessError.NotFound`. Path-form programs remain native launches unchanged.
    [<RequireQualifiedAccess; NoComparison; NoEquality>]
    type PosixLaunch =
        | AsIs
        | DirectPath of ResolvedPath: string

    let resolvePosixLaunch (command: Command) : Result<PosixLaunch, ProcessError> =
        let program = command.Program
        let ctx = commandContext command

        if isBareName program && not ctx.PathIsProcessPath then
            match resolveWith ctx program with
            | Ok resolved -> Ok(PosixLaunch.DirectPath resolved)
            | Error error -> Error error
        else
            Ok PosixLaunch.AsIs

    /// How a Windows spawn should launch `program` once the shared PATHEXT-aware resolver above has had
    /// its say (T-181). It reconciles two `which`/spawn divergences of the same shape — the OS's own
    /// bare-name search cannot reach what this resolution found. First, our `probeDir` finds a bare name
    /// under ANY `PATHEXT` extension, but that search only ever appends `.exe`, so a bare name whose only
    /// match is a `.cmd`/`.bat`/`.com`/… is reported present by `Exec.which` yet unreachable by a raw
    /// `CreateProcessW(lpApplicationName = NULL)`. Second (T-339), that search reads the CURRENT
    /// PROCESS's `PATH` — it resolves the image in the parent's context, never from the child environment
    /// block it is handed — so for a command that overrides or clears the child's `PATH` it would find a
    /// same-named executable somewhere else entirely, or nothing at all. Only meaningful on Windows; a
    /// path-form program, and a bare name resolved against the process's own unchanged `PATH` to an `.exe`
    /// or to nothing, stay `AsIs` — the launch is left byte-for-byte as before and the OS resolves it
    /// exactly as it always did.
    [<RequireQualifiedAccess; NoComparison; NoEquality>]
    type WindowsLaunch =
        /// Launch the program verbatim: a bare name goes to the OS's own `PATH` search (whose richer
        /// application/current/system-directory lookup this `PATH`-only model must not override), and a
        /// path-form program is handed to the OS unchanged. For a bare name this is chosen only while that
        /// OS search walks the very `PATH` this resolution walked, so the two cannot pick different files.
        | AsIs
        /// Substitute the resolved absolute path directly into the launch — a bare name whose only match
        /// carries a non-`.exe`, non-batch executable extension (`.com`/…), a prefer-local match, or any
        /// match resolved against a child `PATH` the command overrode. It is a real image the OS can
        /// spawn directly; it just needs the resolved path because the OS would never find it by bare name.
        | DirectPath of ResolvedPath: string
        /// Route the resolved batch file through `cmd.exe /d /c` — a `.cmd`/`.bat` match, which is not a
        /// PE image and cannot be handed to `CreateProcessW` directly. The caller must apply cmd.exe-safe
        /// argument quoting (BatBadBut / CVE-2024-24576).
        | BatchWrapper of ResolvedPath: string

    /// Decide how a Windows spawn should launch `command`'s program, reusing the SAME
    /// `commandContext`/`findPreferLocal`/`findInContextPath`/`probeDir` resolution the preflight
    /// (`Exec.which`/`Command.ResolveProgram`) goes through — no second copy — so the substitution can
    /// never disagree with what a preflight of the same config reports. The `PATH` walked is the command's
    /// EFFECTIVE child `PATH` (its `Env` override applied), the same block `CreateProcessW` hands the child,
    /// so a bare name reachable only via an overridden `PATH` resolves here exactly as the child would see
    /// it. `AsIs` on every non-Windows platform (there is no `PATHEXT`); a relative path-form match is
    /// substituted by its working-directory-anchored absolute path. A **prefer-local** match
    /// (`Command.PreferLocal`, T-182) is consulted first and is ALWAYS substituted as its resolved absolute
    /// path — even a `.exe`, because the OS would never find it in a prefer-local directory on its own —
    /// with a `.cmd`/`.bat` still routed through the batch wrapper.
    ///
    /// Whether a bare-name `PATH` match may be left to the OS at all turns on ONE fact (T-339): the OS's
    /// bare-name search resolves the image in the PARENT's context, so it walks the current process's
    /// `PATH` and never the child environment block it is handed. While the command leaves the child's
    /// `PATH` alone (`ctx.PathIsProcessPath`), that search and this resolution can only find the same
    /// file, so a `.exe` match stays `AsIs` and the OS's richer application/current/system-directory
    /// lookup is preserved, and a miss is left to the OS too (its failure still flows through
    /// `notFoundFromSpawnFailure` for an honest, `which`-consistent `NotFound`). Once the command changes
    /// or clears that `PATH`, deferring would launch whatever the PARENT's `PATH` happens to hold under
    /// the same name: every match — `.exe` included — is then substituted as its resolved absolute path,
    /// and a miss is refused HERE, before any native spawn, with exactly the `ProcessError.NotFound` and
    /// `Searched` a `Command.ResolveProgram` of the same config reports.
    let resolveWindowsLaunch (command: Command) : Result<WindowsLaunch, ProcessError> =
        let program = command.Program

        if not (RuntimeInformation.IsOSPlatform OSPlatform.Windows) then
            // POSIX has no PATHEXT and resolves a relative path-form after the child-side chdir.
            Ok WindowsLaunch.AsIs
        else
            let ctx = commandContext command

            // The one condition under which the OS's own bare-name search is NOT interchangeable with this
            // resolution: it reads the parent's `PATH`, this reads the child's.
            let childPathOverridden = not ctx.PathIsProcessPath

            // Classify a resolved match by extension. `.cmd`/`.bat` is not a PE image and always routes
            // through `cmd.exe /d /c`. A `PATH` `.exe` stays `AsIs` (the OS appends `.exe` itself, so its
            // richer application/current/system-directory search is preserved) — but only while that
            // search walks the same `PATH`; every prefer-local match, and every match at all once the
            // child's `PATH` is overridden, is instead substituted by absolute path, because the OS would
            // otherwise look somewhere this resolution never did.
            let classify (resolved: string) (preferLocal: bool) : WindowsLaunch =
                let ext = Path.GetExtension resolved

                let isExt (candidate: string) =
                    String.Equals(ext, candidate, StringComparison.OrdinalIgnoreCase)

                if isExt ".cmd" || isExt ".bat" then
                    WindowsLaunch.BatchWrapper resolved
                elif preferLocal || childPathOverridden then
                    WindowsLaunch.DirectPath resolved
                elif isExt ".exe" then
                    WindowsLaunch.AsIs
                else
                    WindowsLaunch.DirectPath resolved

            if isBareName program then
                match findPreferLocal ctx program with
                | Some resolved -> Ok(classify resolved true)
                | None ->
                    match findInContextPath ctx program with
                    | Some resolved, _ -> Ok(classify resolved false)
                    | None, searched ->
                        if childPathOverridden then
                            // The child's `PATH` holds no such program, but the OS would answer from the
                            // parent's — launching a same-named executable this command's own config says
                            // nothing about. Refuse instead, with the very `NotFound` a preflight reports.
                            Error(bareNameNotFound program searched)
                        else
                            // Not found by our resolver either, and the OS searches the same `PATH`: leave
                            // it to the OS, whose failure still flows through `notFoundFromSpawnFailure`
                            // for an honest, `which`-consistent `NotFound`.
                            Ok WindowsLaunch.AsIs
            else
                match resolveWith ctx program with
                | Ok resolved -> Ok(classify resolved true)
                | Error _ -> Ok WindowsLaunch.AsIs
