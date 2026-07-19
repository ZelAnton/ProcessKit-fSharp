namespace ProcessKit

/// A signal to broadcast to every process in a `ProcessGroup` via `ProcessGroup.Signal`.
///
/// The curated variants map to the POSIX signal of the same name on Unix. On **Windows** `Kill` maps
/// to the Job Object terminate (the same hard kill as `ProcessGroup.KillAll`), and `Int`/`Term` are a
/// best-effort soft stop: a console CTRL+BREAK to each child started with `Command.WindowsCtrlSignals()`
/// and a `WM_CLOSE` posted to the top-level windows of every member that has one (a GUI child). They
/// yield `ProcessError.Unsupported` only when the group has neither a CTRL-capable child nor a windowed
/// member — nothing to soft-signal — never a silent downgrade. Every other variant yields
/// `ProcessError.Unsupported` on Windows.
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

    /// A raw signal number, passed through verbatim (Unix only). It must be a real, *deliverable*
    /// signal (`>= 1`): it lands in the *signal* argument of `kill(pid, sig)` (never the pid/target),
    /// so an out-of-range value cannot retarget the signal — it simply fails the send. The number
    /// **0** is rejected rather than sent: `kill`/`killpg` with signal 0 is a *liveness probe* that
    /// only checks the target exists (and the caller may signal it) and delivers nothing, so accepting
    /// it would report a successful delivery while doing nothing. A **negative** number is likewise not
    /// a signal and is refused. Both fail with a typed `ProcessError` (not an exception) — never a
    /// silent success. Always unsupported on Windows.
    | Other of SignalNumber: int
