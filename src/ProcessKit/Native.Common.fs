namespace ProcessKit.Native

open ProcessKit
open System
open System.ComponentModel
open System.IO
open System.Runtime.InteropServices

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
            /// Windows only: `true` when the child was spawned as its own console process group
            /// (`CREATE_NEW_PROCESS_GROUP`), so `ProcessGroup.Signal(Signal.Int/Term)` can deliver a
            /// best-effort `GenerateConsoleCtrlEvent(CTRL_BREAK_EVENT, pid)` to it. Always `false` on
            /// POSIX, which delivers signals through `killpg` regardless of this flag.
            WindowsCtrlGroup: bool
            /// The retained PTY control handle/fd for a `Command.Pty` run, kept for the child's whole
            /// lifetime so `RunningProcess.ResizeAsync` can reach it — the Windows pseudoconsole handle
            /// (`HPCON`, the resize target for `ResizePseudoConsole`) or the POSIX pty MASTER fd (the
            /// `ioctl(TIOCSWINSZ)` target). `None` for every non-PTY spawn, on which `ResizeAsync`
            /// returns a typed `ProcessError.Unsupported` rather than a silent no-op (D6).
            PtyControl: nativeint option
        }

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
    // (`Exec.which` / `CliClient.EnsureAvailableAsync`) and the spawn path's own `ProcessError.NotFound`
    // diagnostic enrichment (populating `Searched` after the OS itself reports a missing program), so
    // the two can never disagree on "found vs not found" for the same program name. The actual spawn
    // call still lets the OS do its own resolution (`CreateProcessW`/`posix_spawnp`) — this logic is
    // never substituted into the real launch path, only used to resolve ahead of time (preflight) or to
    // re-derive the searched-directories diagnostic after a genuine spawn failure.
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
    /// Windows: `PATHEXT`-aware — the bare name is accepted as-is only when it already carries a
    /// recognized executable extension (`git.exe`, `git.cmd`, …); otherwise each `PATHEXT` extension is
    /// tried in order, appended to the bare name. POSIX: the plain file must exist and carry at least
    /// one executable permission bit.
    ///
    /// Exception-safe by construction: this candidate directory can vanish, or a matching file's
    /// permissions can become unreadable, between the `File.Exists` existence check and the follow-up
    /// probe (on POSIX, `File.GetUnixFileMode` — a genuine TOCTOU window, since another process/thread
    /// can delete or replace the file in between). Any such race or access failure on THIS candidate is
    /// caught and treated as "this candidate didn't pan out" (`None`), never a raw exception — so
    /// `findInPath`'s PATH walk simply continues to the next directory instead of aborting the whole
    /// resolution.
    let probeDir (dir: string) (program: string) : string option =
        try
            if RuntimeInformation.IsOSPlatform OSPlatform.Windows then
                let pathExt =
                    match Environment.GetEnvironmentVariable "PATHEXT" with
                    | null
                    | "" -> defaultPathExt
                    | value -> value

                let extensions =
                    pathExt.Split ';'
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
            // this one candidate does not resolve; the PATH walk in `findInPath` continues to the next
            // directory exactly as it would for a plain "not present" candidate.
            None

    /// Search the current process's `PATH` for `program` (a bare name — see `isBareName`), reusing
    /// `probeDir` for each directory in order. Returns `(found, searched)`: `found` is the first
    /// matching directory's resolved path; `searched` is the raw `PATH` value, for the `NotFound`
    /// diagnostic (`""` when `PATH` is unset/empty).
    ///
    /// `PATH` is split on the raw `Path.PathSeparator` with NO quote handling, on Windows as on POSIX —
    /// deliberate and verified. The real bare-name launch is `CreateProcessW` with
    /// `lpApplicationName = NULL` (see `spawnWindowsCore`), which lets the OS resolve the program, and
    /// neither that search nor `SearchPathW` strips surrounding double quotes from a `PATH` entry: a
    /// quoted directory (`"C:\Program Files\Foo"`) is a literal, non-existent path to them, so a program
    /// inside it is unreachable to the actual spawn. Stripping quotes HERE would make this shared
    /// preflight/diagnostic resolver report `found` for a program the real spawn can never launch — the
    /// exact `which`-vs-spawn divergence this resolver exists to prevent, merely inverted. So mirror the
    /// OS: a quoted entry is treated as the (invalid) directory name it literally is and simply matches
    /// nothing, exactly as the spawn finds nothing.
    let findInPath (program: string) : string option * string =
        match Environment.GetEnvironmentVariable "PATH" with
        | null
        | "" -> None, ""
        | pathValue ->
            let dirs = pathValue.Split Path.PathSeparator |> Array.filter (fun d -> d <> "")
            (dirs |> Array.tryPick (fun dir -> probeDir dir program)), pathValue

    /// Resolve `program` to a full path without spawning it. A bare name is looked up via `findInPath`
    /// (typed `ProcessError.NotFound` with `Searched` naming the probed `PATH` value on a miss); a
    /// path-form program is checked directly against its own directory component with the SAME
    /// `probeDir` (so a missing extension is still resolved on Windows), never against `PATH`, and its
    /// `NotFound` carries no `Searched` (nothing was searched — a single candidate location was
    /// checked). This is the single resolution both `Exec.which`/`CliClient.EnsureAvailableAsync` and
    /// the spawn path's `NotFound` enrichment (`notFoundFromSpawnFailure` below) go through, so
    /// preflight and an actual spawn never disagree.
    ///
    /// Never throws: `probeDir` already absorbs per-candidate IO/access races (see its own doc comment),
    /// so a raw exception surfacing here would be something unexpected at the level of the whole
    /// resolution (not tied to one candidate) — the outer `with` below is that last-resort net, turning
    /// it into a typed `ProcessError.Io` instead of letting it escape the Result-returning contract that
    /// `Exec.which`/`CliClient.EnsureAvailableAsync` promise their callers.
    let resolveProgram (program: string) : Result<string, ProcessError> =
        try
            if String.IsNullOrWhiteSpace program then
                Error(ProcessError.NotFound(program, None))
            elif isBareName program then
                match findInPath program with
                | Some found, _ -> Ok found
                | None, searched ->
                    Error(ProcessError.NotFound(program, (if searched = "" then None else Some searched)))
            else
                let directory =
                    match Path.GetDirectoryName program with
                    | null
                    | "" -> "."
                    | d -> d

                let fileName =
                    match Path.GetFileName program with
                    | null -> program
                    | name -> name

                match probeDir directory fileName with
                | Some found -> Ok found
                | None -> Error(ProcessError.NotFound(program, None))
        with ex ->
            Error(ProcessError.Io $"failed to resolve '{program}': {ex.Message}")

    /// Enrich a spawn-time not-found failure with the `Searched` diagnostic, reusing `resolveProgram`
    /// so the spawn path and the preflight helper can never disagree. The OS itself already reported
    /// `program` as not found; if this redo unexpectedly resolves it anyway, that is an honest
    /// `ProcessError.Spawn`, never a false `NotFound` — two known ways this can happen: a matching file
    /// exists but is not directly executable (a permissions issue), or (Windows only) `resolveProgram`'s
    /// full `PATHEXT` search matched a `.bat`/`.cmd` sibling that raw `CreateProcess` itself can never
    /// launch without a shell (it only auto-appends `.exe`) — a real, if narrow, `which`-vs-spawn gap
    /// this reports honestly rather than silently.
    let notFoundFromSpawnFailure (program: string) : ProcessError =
        match resolveProgram program with
        | Ok resolved ->
            ProcessError.Spawn(
                program,
                $"the OS reported the program as not found, but it resolves locally to '{resolved}' — check that it is directly executable (a .bat/.cmd match needs a shell to run; otherwise check its executable permissions)"
            )
        | Error error -> error

    /// How a Windows spawn should launch `program` once the shared PATHEXT-aware resolver above has had
    /// its say (T-181). It reconciles a `which`/spawn divergence: our `probeDir` finds a bare name under
    /// ANY `PATHEXT` extension, but the OS's own bare-name `PATH` search only ever appends `.exe`, so a
    /// bare name whose only match is a `.cmd`/`.bat`/`.com`/… is reported present by `Exec.which` yet
    /// unreachable by a raw `CreateProcessW(lpApplicationName = NULL)`. Only meaningful on Windows; every
    /// other case (a `.exe` match, a path-form program, a name that resolves to nothing) is `AsIs` — the
    /// launch is left byte-for-byte as before and the OS resolves it exactly as it always did.
    [<RequireQualifiedAccess; NoComparison; NoEquality>]
    type WindowsLaunch =
        /// Launch the program verbatim: a bare name goes to the OS's own `PATH` search (whose richer
        /// application/current/system-directory lookup this `PATH`-only model must not override), and a
        /// path-form program is handed to the OS unchanged.
        | AsIs
        /// Substitute the resolved absolute path directly into the launch — a bare name whose only match
        /// carries a non-`.exe`, non-batch executable extension (`.com`/…). It is a real image the OS can
        /// spawn directly; it just needs the resolved path because the OS would never find it by bare name.
        | DirectPath of ResolvedPath: string
        /// Route the resolved batch file through `cmd.exe /d /c` — a `.cmd`/`.bat` match, which is not a
        /// PE image and cannot be handed to `CreateProcessW` directly. The caller must apply cmd.exe-safe
        /// argument quoting (BatBadBut / CVE-2024-24576).
        | BatchWrapper of ResolvedPath: string

    /// Decide how a Windows spawn should launch `program`, reusing the SAME `findInPath`/`probeDir`
    /// resolution the preflight (`Exec.which`/`resolveProgram`) goes through — no second copy — so the
    /// substitution can never disagree with what `which` reports. `AsIs` on every non-Windows platform
    /// (there is no `PATHEXT`) and for a path-form program or a name that resolves to nothing; a `.exe`
    /// match is deliberately left `AsIs` so the OS's own bare-name search still applies.
    let resolveWindowsLaunch (program: string) : WindowsLaunch =
        if
            not (RuntimeInformation.IsOSPlatform OSPlatform.Windows)
            || not (isBareName program)
        then
            // POSIX has no PATHEXT (the OS resolves a bare name exactly as `probeDir` models it), and a
            // path-form program is handed to the OS verbatim — never rewritten. Both stay byte-for-byte.
            WindowsLaunch.AsIs
        else
            match findInPath program |> fst with
            | None ->
                // Not found by our resolver either: leave it to the OS, whose failure still flows through
                // `notFoundFromSpawnFailure` for an honest, `which`-consistent `NotFound`.
                WindowsLaunch.AsIs
            | Some resolved ->
                let ext = Path.GetExtension resolved

                let isExt (candidate: string) =
                    String.Equals(ext, candidate, StringComparison.OrdinalIgnoreCase)

                if isExt ".exe" then
                    // The one extension the OS's bare-name search appends itself: leave the bare name so
                    // that richer OS lookup (application/current/system directories) is never overridden.
                    WindowsLaunch.AsIs
                elif isExt ".cmd" || isExt ".bat" then
                    WindowsLaunch.BatchWrapper resolved
                else
                    WindowsLaunch.DirectPath resolved
