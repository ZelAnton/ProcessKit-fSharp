namespace ProcessKit

open System

/// An enriched, point-in-time snapshot of one member of a `ProcessGroup` (see
/// `ProcessGroup.MembersInfo`). `Pid` is always present — it is how the group enumerated the member;
/// every other field is `option` and is `None` wherever the platform cannot honestly report it, never a
/// fabricated value.
///
/// The member's **command line and environment are deliberately absent on every platform** — argv
/// routinely carries secrets and redaction/hashing is the consumer's policy, so this snapshot excludes
/// them by construction, the same exclusion the logging / tracing / metrics paths enforce.
///
/// Sealed with an internal constructor so it can gain fields without breaking the frozen API.
[<Sealed>]
type MemberInfo internal (pid: int, ppid: int option, exeName: string option, startTime: DateTime option) =

    /// The member's process id. Always present.
    member _.Pid = pid

    /// The member's parent process id, when the platform can report it (Windows: the process snapshot's
    /// parent id; Linux: `/proc/<pid>/stat`; macOS: `proc_pidinfo`). `None` where no reader is available
    /// (a BSD other than macOS) or the value could not be parsed.
    member _.Ppid = ppid

    /// The member's executable image name — a base name, never a full argv (Windows: the process
    /// snapshot's image file name, e.g. `node.exe`; Linux: the `/proc` `comm`, truncated to ~15 chars;
    /// macOS: `proc_pidinfo`'s command name). `None` where the platform cannot report it.
    member _.ExeName = exeName

    /// The member's OS-reported start time (`System.Diagnostics.Process.StartTime`, local kind). `None`
    /// when the process exited before this read, or the start time is inaccessible on this platform/timing.
    member _.StartTime = startTime
