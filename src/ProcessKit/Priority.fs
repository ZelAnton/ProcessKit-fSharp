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
/// **How far the priority reaches into the spawned tree depends on the platform and the level.** It
/// always takes on the immediate child on both platforms; whether the child's own descendants
/// (grandchildren) inherit it differs:
///
/// - **Unix — whole tree, every level.** A `nice` value is inherited across `fork`, so every
///   descendant the leader spawns runs at the requested priority. One honest divergence: the `nice`
///   is applied to the group leader *immediately after* `posix_spawn` returns (there is no
///   `posix_spawn` attribute for it), so a descendant the leader forks in the sub-millisecond window
///   before that call lands keeps the inherited default — the same spawn→apply window the cgroup
///   mechanism already documents.
///
/// - **Windows — whole tree only for the lowered classes.** The priority class is set atomically at
///   process creation (no spawn→apply window), but Windows only *inherits* a class to grandchildren
///   when it is lowered. Per `CreateProcess`, a child spawned with no priority-class flag defaults to
///   `NORMAL_PRIORITY_CLASS` *unless* its creator is `IDLE_PRIORITY_CLASS` or
///   `BELOW_NORMAL_PRIORITY_CLASS`, in which case it inherits that class. So `Idle`/`BelowNormal`
///   (and `Normal`) reach the whole tree, but for `AboveNormal`/`High` the grandchildren a child
///   later spawns run at `Normal`, not the requested elevated class. The elevation is still honored
///   on the immediate child for all five levels — only its inheritance by grandchildren is the
///   platform limit, and it is never a silent downgrade of the child you launched.
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

    /// The default OS priority — Windows `NORMAL_PRIORITY_CLASS`, Unix `nice(0)`. When the launching
    /// process itself runs at the default priority (the usual case), setting this is functionally
    /// equivalent to not calling `Command.Priority` at all. It maps to an *absolute* target, not a
    /// "leave as inherited": on Unix it is `setpriority` to nice `0`, so under a launcher that is
    /// itself niced above `0` it lowers the child's nice back to `0` — which needs privilege exactly
    /// as `AboveNormal`/`High` do (and fails the spawn without it, never silently) rather than keeping
    /// the raised nice.
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
