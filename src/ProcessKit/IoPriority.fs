namespace ProcessKit

open System
open System.Collections.Generic

/// The **Linux I/O-scheduling class** a child's disk work runs in — the class half of an
/// `IoPriority`, and the axis `ionice(1)`'s `-c` option selects. A separate dimension from the
/// portable CPU-scheduling `Priority`: that one decides how much *processor* the child gets, this one
/// how its *block-device* requests are ordered against everyone else's. Neither implies the other, and
/// a background job usually wants both.
///
/// **Linux-only, and honestly so.** `ioprio_set(2)` is a Linux system call with no POSIX or Win32
/// equivalent, so a spawn carrying an `IoPriority` fails with `ProcessError.Unsupported` on Windows,
/// macOS, and the BSDs rather than running the child at the inherited priority as if the request had
/// been honoured (see `Command.IoPriority`).
[<RequireQualifiedAccess; NoComparison>]
type IoPriorityClass =

    /// The child's I/O runs **only while the block device is otherwise idle** (`IOPRIO_CLASS_IDLE`).
    /// The politest setting there is, and the right one for bulk background work — a backup, an
    /// indexer, a large checkout — that must never slow down an interactive user. It carries no level
    /// (the kernel ignores the level field for this class).
    ///
    /// Needs no privilege: an ordinary user may put its own children in this class.
    | Idle

    /// The ordinary Linux class every process is in unless something moved it
    /// (`IOPRIO_CLASS_BE`), with a level from `0` (highest priority) through `7` (lowest). A process
    /// that never asked for a class gets a best-effort level derived from its `nice` value; naming a
    /// level here makes that choice explicit and independent of `nice`.
    ///
    /// Needs no privilege in either direction — this is the class to reach for when `Idle` is too
    /// severe (a job that should yield but still make steady progress is `BestEffort 7`).
    | BestEffort

    /// The Linux **real-time** I/O class (`IOPRIO_CLASS_RT`), with a level from `0` (highest priority)
    /// through `7` (lowest). Requests in this class are served ahead of every best-effort and idle
    /// request on the device, so a busy child here can starve the rest of the system — including
    /// processes that are not yours. Prefer `BestEffort` unless a genuine latency requirement says
    /// otherwise.
    ///
    /// **Needs privilege:** `CAP_SYS_ADMIN` (Linux ≥ 5.14 accepts `CAP_SYS_NICE` as well). Without it
    /// the kernel refuses the request and the spawn fails with `ProcessError.Spawn` — it is never
    /// quietly downgraded to best-effort.
    | RealTime

    /// Every class, in a fixed order — the enumerable form of the set `Name`/`TryFromName` map between,
    /// so a config layer can validate or document the accepted spellings without keeping its own copy of
    /// the list (which could silently fall behind a new class).
    static member All: IReadOnlyList<IoPriorityClass> =
        [| IoPriorityClass.Idle; IoPriorityClass.BestEffort; IoPriorityClass.RealTime |]

    /// This class's **stable machine identifier**: a short, lowercase `snake_case` string, part of the
    /// library's compatibility surface. Use it wherever a class has to travel as text — a config file's
    /// key, a CLI flag, a structured log field — instead of hand-maintaining a mapping table. It is a
    /// diagnostic identifier rather than a wire format, but it is held stable all the same: a new class
    /// gets a NEW identifier and an existing one is never renamed within a major version. `TryFromName`
    /// parses it back. Deliberately ProcessKit's own spelling rather than `ionice`'s numeric `-c`
    /// argument: the number is one tool's CLI detail, and the two must be free to differ.
    member this.Name: string =
        match this with
        | IoPriorityClass.Idle -> "idle"
        | IoPriorityClass.BestEffort -> "best_effort"
        | IoPriorityClass.RealTime -> "real_time"

    /// Parse a stable `Name` identifier back into a class, or `None` for anything that is not EXACTLY
    /// one of them (matching is ordinal and case-sensitive: `"BestEffort"` and `"besteffort"` are both
    /// misses, only `"best_effort"` hits). An honest miss, never a silent default — a config-driven
    /// caller that mistypes a class gets nothing back to apply, instead of its child landing in a
    /// different class than the one that was written down. Round-trips with `Name` for every class.
    static member TryFromName(name: string) : IoPriorityClass option =
        IoPriorityClass.All |> Seq.tryFind (fun ioClass -> ioClass.Name = name)

    /// `TryFromName` for a caller that wants the miss as an error instead of an option — an unknown name
    /// raises `ArgumentException` listing every accepted spelling, so a mistyped config key fails where
    /// it is read rather than silently leaving the child at the inherited I/O priority.
    /// `null` raises `ArgumentNullException`.
    static member FromName(name: string) : IoPriorityClass =
        ArgumentNullException.ThrowIfNull(name, nameof name)

        match IoPriorityClass.TryFromName name with
        | Some ioClass -> ioClass
        | None ->
            let accepted =
                IoPriorityClass.All
                |> Seq.map (fun ioClass -> ioClass.Name)
                |> String.concat ", "

            raise (
                ArgumentException(
                    $"'{name}' is not a known I/O scheduling class; expected one of: {accepted}",
                    nameof name
                )
            )

/// One Linux I/O-scheduling priority as configured through `Command.IoPriority`: an
/// `IoPriorityClass` and, for the two levelled classes, the level within it. The pair
/// `ioprio_set(2)` itself encodes into a single value.
///
/// Built only through the three validating factories below — `Idle`, `BestEffort level`,
/// `RealTime level` — which reject an out-of-range level at that boundary with
/// `ArgumentOutOfRangeException` rather than clamping it into range or handing the kernel a value it
/// would refuse later. **Lower levels mean higher priority**, which is the kernel's own convention and
/// the opposite of how `Priority` reads: `BestEffort 0` is the most aggressive best-effort setting and
/// `BestEffort 7` the politest.
[<Sealed>]
type IoPriority internal (ioClass: IoPriorityClass, level: int) =

    /// The highest level number the kernel accepts (`IOPRIO_NR_LEVELS - 1`). Levels run `0`..`MaxLevel`,
    /// lowest number = highest priority. Published so a config layer can validate a level it read as
    /// text against the same bound the factories enforce, instead of repeating the number.
    static member MaxLevel = 7

    /// The class this priority is in.
    member _.Class = ioClass

    /// The level within `Class`, `0` (highest priority) through `MaxLevel` (lowest). Reads `0` for
    /// `IoPriorityClass.Idle`, which has no level at all — the kernel ignores the level field in that
    /// class, so there is no meaningful number to report and `0` is the value that is sent.
    member _.Level = level

    /// The politest setting: I/O only while the block device is otherwise idle
    /// (`IoPriorityClass.Idle`). Needs no privilege.
    static member Idle = IoPriority(IoPriorityClass.Idle, 0)

    /// The ordinary best-effort class at `level` (`0` highest priority through `MaxLevel` lowest).
    /// `BestEffort 7` is the usual choice for work that should yield to interactive users but still
    /// make steady progress. Needs no privilege.
    ///
    /// A `level` outside `0..MaxLevel` raises `ArgumentOutOfRangeException` here, at the construction
    /// boundary — never clamped into range, and never carried as far as the kernel.
    static member BestEffort(level: int) : IoPriority =
        IoPriority.Validated(IoPriorityClass.BestEffort, level)

    /// The real-time class at `level` (`0` highest priority through `MaxLevel` lowest). **Needs
    /// `CAP_SYS_ADMIN`** (or `CAP_SYS_NICE` on Linux ≥ 5.14) and can starve every other disk user — see
    /// `IoPriorityClass.RealTime` before reaching for it.
    ///
    /// A `level` outside `0..MaxLevel` raises `ArgumentOutOfRangeException` here, at the construction
    /// boundary — never clamped into range, and never carried as far as the kernel.
    static member RealTime(level: int) : IoPriority =
        IoPriority.Validated(IoPriorityClass.RealTime, level)

    /// The shared range check the two levelled factories perform. Private so that the only way to build
    /// an `IoPriority` from outside this assembly is through a factory that has validated its level.
    static member private Validated(ioClass: IoPriorityClass, level: int) : IoPriority =
        if level < 0 || level > IoPriority.MaxLevel then
            raise (
                ArgumentOutOfRangeException(
                    nameof level,
                    level,
                    $"a Linux {ioClass.Name} I/O priority level must be in 0..{IoPriority.MaxLevel} (lower is higher priority)"
                )
            )

        IoPriority(ioClass, level)

    /// The canonical one-line rendering: the class's stable `Name` alone for `Idle` (which has no
    /// level), and `<name>:<level>` for the levelled classes (e.g. `best_effort:7`) — what a diagnostic
    /// such as a dry-run preview shows. Carries no argv or environment value, so it is safe to log.
    override _.ToString() =
        match ioClass with
        | IoPriorityClass.Idle -> ioClass.Name
        | _ -> $"{ioClass.Name}:{level}"

    /// Value equality: two priorities are equal when they name the same class and level. Spelled out
    /// because the factories hand back a FRESH instance per call, so without it even
    /// `IoPriority.Idle = IoPriority.Idle` would be false — a configuration compared against a default,
    /// or one read twice, must not depend on which object it happens to be.
    override this.Equals(other: objnull) =
        match other with
        | :? IoPriority as that -> this.Class = that.Class && this.Level = that.Level
        | _ -> false

    override _.GetHashCode() = HashCode.Combine(ioClass, level)

/// Internal mapping from the public `IoPriority` onto the single integer `ioprio_set(2)` takes. Kept
/// here rather than inlined into the POSIX native layer, for the same reason `PriorityMapping` is: the
/// encoding is then directly unit-testable without spawning anything, and the one place that spells the
/// kernel's class numbers is next to the type they describe.
module internal IoPriorityMapping =

    /// How far the class is shifted left in the encoded value (`IOPRIO_CLASS_SHIFT`). The low 13 bits
    /// carry the level, so a level of `0..7` never collides with the class field.
    [<Literal>]
    let ClassShift = 13

    /// The kernel's own class numbers (`linux/ioprio.h`). Deliberately NOT the `IoPriorityClass.Name`
    /// identifiers: those are ProcessKit's published vocabulary, these are the ABI, and the two must be
    /// free to change independently.
    let classNumber (ioClass: IoPriorityClass) : int =
        match ioClass with
        | IoPriorityClass.RealTime -> 1
        | IoPriorityClass.BestEffort -> 2
        | IoPriorityClass.Idle -> 3

    /// The single value `ioprio_set(2)` takes: `IOPRIO_PRIO_VALUE(class, level)`, i.e. the class number
    /// shifted up by `ClassShift` with the level in the low bits. The level is validated to `0..7` at
    /// the `IoPriority` construction boundary, so it can never overflow into the class field here.
    let linuxValue (priority: IoPriority) : int =
        (classNumber priority.Class <<< ClassShift) ||| priority.Level
