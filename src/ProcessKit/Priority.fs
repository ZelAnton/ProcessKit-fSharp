namespace ProcessKit

/// A portable CPU-scheduling priority for a child process (see `Command.Priority`), mapped onto the
/// native primitive at spawn time: a **Windows** process priority class OR'd into the `CreateProcess`
/// creation flags (the same seam as `Command.CreateNoWindow`), or a **Unix** `nice` value applied via
/// `setpriority` to the spawned process-group leader.
///
/// Every variant is supported on **both** platform families — `setpriority` is plain POSIX (Linux,
/// macOS, the BSDs alike) and every Windows edition has all five priority classes — so
/// `Command.Priority` never yields `ProcessError.Unsupported`.
///
/// **Applies to the whole spawned tree, not just the immediate child.** A Windows child inherits its
/// creator's priority class, and a Unix `nice` value is inherited across `fork`, so every descendant
/// the child later spawns runs at the requested priority. One honest divergence: on Unix the `nice`
/// is applied to the group leader *immediately after* `posix_spawn` returns (there is no
/// `posix_spawn` attribute for it), so a descendant the leader forks in the sub-millisecond window
/// before that call lands keeps the inherited default — the same spawn→apply window the cgroup
/// mechanism already documents. On Windows the priority class is set atomically at process creation,
/// so there is no such window.
///
/// Only ordinary (non-real-time) priorities are exposed; `Priority` never raises a real-time class,
/// and I/O scheduling is out of scope.
[<RequireQualifiedAccess; NoComparison>]
type Priority =

    /// Lowest scheduling priority — runs only when the system is otherwise idle.
    /// Windows `IDLE_PRIORITY_CLASS`; Unix `nice(19)`.
    | Idle

    /// Below the default priority — polite background work that still makes steady progress.
    /// Windows `BELOW_NORMAL_PRIORITY_CLASS`; Unix `nice(10)`.
    | BelowNormal

    /// The default OS priority. Setting this explicitly is functionally equivalent to not calling
    /// `Command.Priority` at all. Windows `NORMAL_PRIORITY_CLASS`; Unix `nice(0)`.
    | Normal

    /// Above the default priority. Windows `ABOVE_NORMAL_PRIORITY_CLASS`; Unix `nice(-5)`.
    | AboveNormal

    /// Highest ordinary (non-real-time) priority. Windows `HIGH_PRIORITY_CLASS`; Unix `nice(-10)`.
    ///
    /// **Unix caveat:** lowering `nice` below its inherited value needs `CAP_SYS_NICE` (Linux) or an
    /// equivalent privilege elsewhere. Without it the OS refuses the change and the spawn fails with
    /// `ProcessError.Spawn` — it is never silently downgraded to a lower priority. Windows needs no
    /// special privilege for the high class.
    | High

/// Internal mappings from the portable `Priority` onto each platform's native primitive. Kept in one
/// place (rather than inlined into the Windows/POSIX native layers) so the mapping is directly
/// unit-testable and the two platform tables stay side by side.
module internal PriorityMapping =

    /// The `nice` value applied via `setpriority(PRIO_PROCESS, ...)` on Unix. Lower is higher priority;
    /// `setpriority` sets the *absolute* nice, so `Normal` maps to an absolute `0`, not a relative bump.
    let niceValue (priority: Priority) : int =
        match priority with
        | Priority.Idle -> 19
        | Priority.BelowNormal -> 10
        | Priority.Normal -> 0
        | Priority.AboveNormal -> -5
        | Priority.High -> -10

    // Windows process priority-class creation flags (winbase.h). OR'd into the `CreateProcess`
    // creation flags at spawn — the same seam as `CREATE_NO_WINDOW`.
    [<Literal>]
    let private IDLE_PRIORITY_CLASS = 0x00000040u

    [<Literal>]
    let private BELOW_NORMAL_PRIORITY_CLASS = 0x00004000u

    [<Literal>]
    let private NORMAL_PRIORITY_CLASS = 0x00000020u

    [<Literal>]
    let private ABOVE_NORMAL_PRIORITY_CLASS = 0x00008000u

    [<Literal>]
    let private HIGH_PRIORITY_CLASS = 0x00000080u

    /// The Windows priority-class flag OR'd into `CreateProcess`'s creation flags.
    let windowsCreationFlag (priority: Priority) : uint32 =
        match priority with
        | Priority.Idle -> IDLE_PRIORITY_CLASS
        | Priority.BelowNormal -> BELOW_NORMAL_PRIORITY_CLASS
        | Priority.Normal -> NORMAL_PRIORITY_CLASS
        | Priority.AboveNormal -> ABOVE_NORMAL_PRIORITY_CLASS
        | Priority.High -> HIGH_PRIORITY_CLASS
