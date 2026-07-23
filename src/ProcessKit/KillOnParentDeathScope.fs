namespace ProcessKit

open System.Runtime.InteropServices

/// The *scope* of cleanup a platform actually guarantees when the parent process of a
/// `Command.KillOnParentDeath` child dies **suddenly** (SIGKILL, a crash, `TerminateProcess`) —
/// reported honestly, fixed per platform, and **independent of whether `KillOnParentDeath()` was
/// called**. This is the same "report the real guarantee rather than paper over a downgrade"
/// principle as `ProcessGroup.Mechanism`: a caller can reason about what actually happens on the
/// current OS instead of assuming a uniform whole-tree kill everywhere.
[<RequireQualifiedAccess; NoComparison>]
type KillOnParentDeathScope =

    /// The **whole process tree** is reaped on sudden parent death (Windows). Every child ProcessKit
    /// starts lives inside a Job Object created with `KILL_ON_JOB_CLOSE`, and the parent process owns
    /// the only handle to that Job. When the parent terminates for *any* reason, the kernel closes its
    /// handles as part of process rundown; closing the last Job handle terminates every process in the
    /// Job. So the guarantee already holds tree-wide and unconditionally — `KillOnParentDeath()` needs
    /// no extra action on Windows.
    | WholeTree

    /// Only the **direct child** is reaped (Linux). `PR_SET_PDEATHSIG` — armed via the
    /// `setpriv --pdeathsig` helper — delivers `SIGKILL` to the immediate child when its parent dies,
    /// but the parent-death signal is **not inherited** across a `fork`: a grandchild the child forks
    /// has its own (unset) parent-death signal, so once the child's parent is gone nothing reaps the
    /// grandchildren's cgroup/pgroup. See `Command.KillOnParentDeath` for the further caveats (the
    /// set-uid/set-gid `execve` reset, and the spawning-thread lifetime note).
    | DirectChildOnly

    /// **Nothing** is reaped on sudden parent death (macOS/BSD). There is no `PR_SET_PDEATHSIG` analog,
    /// so `Command.KillOnParentDeath()` is refused with a typed `ProcessError.Unsupported` on the POSIX
    /// spawn path rather than silently pretending the cleanup will happen.
    | Nothing

    /// The scope of `Command.KillOnParentDeath` cleanup fixed for the **current** OS: Windows →
    /// `WholeTree` (Job Object `KILL_ON_JOB_CLOSE`), Linux → `DirectChildOnly` (`PR_SET_PDEATHSIG` via
    /// `setpriv`), macOS/BSD → `Nothing` (no `pdeathsig` analog). This is the single source of truth the
    /// public `Command.KillOnParentDeathScope()` reports; it deliberately mirrors the per-platform gate
    /// the POSIX spawn path applies (`Native.Posix.spawnPosix` refuses the request off Linux) so the
    /// report can never drift from what the spawn actually does.
    static member internal Current: KillOnParentDeathScope =
        if RuntimeInformation.IsOSPlatform OSPlatform.Windows then
            KillOnParentDeathScope.WholeTree
        elif RuntimeInformation.IsOSPlatform OSPlatform.Linux then
            KillOnParentDeathScope.DirectChildOnly
        else
            KillOnParentDeathScope.Nothing
