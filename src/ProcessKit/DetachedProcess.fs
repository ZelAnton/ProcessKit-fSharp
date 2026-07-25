namespace ProcessKit

open System

/// A child launched **outside** every containment primitive by `Command.LaunchDetached` / `Exec.detach`
/// — the library's single, deliberate opt-out from the kill-on-dispose guarantee, for the spawn-and-forget
/// cases containment makes impossible: a self-updater that must outlive the process it replaces, a
/// restart-myself relaunch, a daemon/agent handed off to the OS.
///
/// **This is a diagnostic snapshot, not a handle.** It owns nothing, holds no OS handle, and has no
/// `Dispose`: the child is no longer ProcessKit's to manage, so there is deliberately nothing here to
/// wait on, stream, signal, or kill. Everything a contained run gives you — `RunningProcess`, the
/// `Outcome`, the Job Object / cgroup / process-group teardown — is what you traded away by calling the
/// detached verb; go through `StartAsync`/`RunAsync` (or a `ProcessGroup`) if you want any of it back.
/// If you later need to reach this process anyway, do it through the OS with the identity below
/// (`System.Diagnostics.Process.GetProcessById`, a pid file, a service manager), accepting the
/// pid-reuse risk that ProcessKit's own containment exists to eliminate.
///
/// **`Pid` alone is not an identity.** A pid is reused by the OS once the process is gone, so a bare pid
/// read later can name an unrelated process. `StartTime` is the standard disambiguator: the pair
/// (`Pid`, `StartTime`) identifies this specific incarnation, and re-reading a live process's start time
/// is how you check that a pid still refers to *this* child before acting on it. The pair is captured at
/// launch, while the pid was still pinned (Windows: our own open process handle; POSIX: the child is
/// unreaped), so it can never describe an already-recycled pid.
///
/// Sealed with an internal constructor so it can gain fields without breaking the frozen API. Like
/// `MemberInfo`, it deliberately carries **no command line and no environment** — only the program name
/// already present on `ProcessError` — since argv routinely carries secrets.
[<Sealed>]
type DetachedProcess internal (pid: int, program: string, startTime: DateTime option) =

    /// The detached child's process id, as reported by the OS at launch. Always present — the launch
    /// either produced a real process or returned a typed `ProcessError`.
    member _.Pid = pid

    /// The program as it was launched (the `Command.Program` name, never the full argv).
    member _.Program = program

    /// The child's OS-reported start time (`System.Diagnostics.Process.StartTime`, local kind), read at
    /// launch. `None` when the platform/timing could not report it honestly — never a fabricated value.
    /// Paired with `Pid`, this is the identity that survives pid reuse (see the type-level note).
    member _.StartTime = startTime

    /// A short, non-secret description for logs and diagnostics: the program name and pid only.
    override _.ToString() = $"detached {program} (pid {pid})"
