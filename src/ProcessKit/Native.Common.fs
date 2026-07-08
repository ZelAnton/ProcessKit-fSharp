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
