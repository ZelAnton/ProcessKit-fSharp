namespace ProcessKit

/// A signal to broadcast to every process in a `ProcessGroup` via `ProcessGroup.Signal`.
///
/// The curated variants map to the POSIX signal of the same name on Unix. On **Windows** only
/// `Kill` is deliverable (it maps to the Job Object terminate, the same hard kill as
/// `ProcessGroup.KillAll`); every other variant yields `ProcessError.Unsupported`.
///
/// `Other` is an escape hatch carrying a raw signal number on Unix (e.g. `SIGWINCH`); it is always
/// unsupported on Windows.
///
/// `SIGSTOP`/`SIGCONT` are deliberately absent from the curated set — pause and resume the whole
/// tree with `ProcessGroup.Suspend` / `ProcessGroup.Resume`, which are portable (Windows included);
/// `Signal.Other` with the raw `SIGSTOP` number remains available when the raw signal is
/// specifically wanted on Unix.
[<RequireQualifiedAccess; NoComparison>]
type Signal =

    /// `SIGTERM` — polite request to exit.
    | Term

    /// `SIGKILL` — unblockable kill. On Windows: terminate the Job Object.
    | Kill

    /// `SIGINT` — keyboard interrupt.
    | Int

    /// `SIGHUP` — hangup; conventionally "reload configuration".
    | Hup

    /// `SIGQUIT` — quit, typically with a core dump.
    | Quit

    /// `SIGUSR1` — user-defined.
    | Usr1

    /// `SIGUSR2` — user-defined.
    | Usr2

    /// A raw signal number, passed through verbatim (Unix only). It must be a valid signal; it
    /// lands in the *signal* argument of `kill(pid, sig)` (never the pid/target), so an
    /// out-of-range value simply fails the send — it cannot retarget the signal.
    | Other of SignalNumber: int
