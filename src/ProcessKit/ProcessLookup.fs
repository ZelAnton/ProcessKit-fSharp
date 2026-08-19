namespace ProcessKit

open System
open System.Runtime.InteropServices

/// Standalone, identity-safe process lookup and reuse-safe liveness for a pid the caller holds
/// **outside** any `ProcessGroup` (T-385) — a pid saved to disk across runs, a launch registry, or an
/// external probe watching a process this library never itself contained. `ProcessGroup.MembersInfo`
/// answers the same questions for a group's own membership; this module is the companion for a bare pid
/// nothing here has ever tracked, so it needs no group and creates nothing.
///
/// Reuses exactly the same per-platform readers `ProcessGroup.MembersInfo` uses (`Native.Windows` /
/// `Native.Posix`) — no second, parallel identity-reading mechanism for this entry point — and keeps
/// every standing rule those readers already enforce: never reads a process's argv or environment, and
/// every enriching `MemberInfo` field stays an honest `option`, `None` wherever the platform cannot
/// report it, never fabricated.
[<RequireQualifiedAccess>]
module ProcessLookup =

    /// Look up the identity and best-effort metadata of an **arbitrary** process by pid — the standalone
    /// companion to `ProcessGroup.MembersInfo`, for a pid the caller holds outside any group.
    ///
    /// Three honest outcomes, never confused with one another:
    /// - `Ok(Some info)` — the process exists; `info` carries the same fields `MemberInfo` always does
    ///   (`Ppid` / `ExeName` / `StartTime`, each `None` where the platform cannot honestly report it).
    /// - `Ok None` — the pid names **no** process: an honest negative, not an error — the "it's gone"
    ///   answer a liveness check wants.
    /// - `Error` — the process may well exist, but its state could not be determined (denied permission,
    ///   an OS read failure). **Never** read this as "dead" — that is the whole reason it is an error
    ///   rather than `Ok None`.
    ///
    /// `pid <= 0` is refused up front with `Ok None`, before any native call: `0` names Windows' own
    /// unopenable System Idle Process, and on POSIX `0`/a negative number addresses "the caller's own
    /// process group" / a process GROUP rather than an individual process — neither is a meaning this
    /// read-only query exists to act on. This process's own pid is not special-cased further: unlike
    /// `ProcessGroup.AdoptByPid` (which would enlist the caller in its own group's teardown), a read-only
    /// lookup has nothing to enlist it into, so querying yourself is an entirely ordinary, exercised case.
    ///
    /// Never reads the process's command line or environment, on any platform — the same exclusion
    /// `MemberInfo` documents. A snapshot taken now: the process may exit immediately afterwards, and the
    /// pid is only as stable as the OS's reuse policy — to tell a *recycled* number apart from the
    /// original process later, pair the returned `StartTime` with the pid and use `processIsAlive`.
    ///
    /// See `docs/platform-support.md` for the per-platform existence/permission oracle each reader uses
    /// (Windows: `OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION)`; Linux: `/proc/<pid>/stat`; macOS:
    /// `proc_pidinfo`; the bare BSDs: a zero-signal `kill(pid, 0)` probe).
    let processInfo (pid: int) : Result<MemberInfo option, ProcessError> =
        if pid <= 0 then
            Ok None
        elif RuntimeInformation.IsOSPlatform OSPlatform.Windows then
            Native.Windows.processInfo pid
        else
            Native.Posix.processInfo pid

    /// Reuse-safe liveness: is the process at `pid` **still the same instance** you saw earlier — the one
    /// whose `MemberInfo.StartTime` you saved (from an earlier `processInfo`, or `ProcessGroup
    /// .MembersInfo`)?
    ///
    /// Because the OS reuses pid *numbers*, a bare pid check would answer "alive" for a stranger that
    /// recycled the number after your process exited; pairing it with the start time — fixed at creation
    /// and distinct for a later occupant — tells the original apart from a recycled number. This is the
    /// same anti-reuse discipline `ProcessGroup.AdoptByPid` applies internally to its own kills and stats
    /// reads, exposed here for a pid you merely hold.
    ///
    /// - `Ok true` — the process at `pid` exists **and**, when both start-time tokens are known, they
    ///   agree: your process is still running.
    /// - `Ok false` — the process is gone: either the pid names nothing, or it names a **different**
    ///   process now (a recycled number — the start times differ), so *your* process is no longer alive.
    /// - `Error` — the pid may name a live process but it could not be inspected (the same permission/OS-
    ///   error surface as `processInfo`). Never read this as "dead".
    ///
    /// **Reuse protection degrades honestly.** The recycle check needs a start-time token on **both**
    /// sides. When `startTime` is `None` (nothing was saved — e.g. it originated on a platform with no
    /// per-pid reader) or the platform reports none for the live process at `pid` right now, a recycle
    /// cannot be *proven*, so this degrades to bare-pid liveness: a live process at the number reads as
    /// `Ok true`. So on a platform that reports a start time (Windows, Linux, macOS), passing the saved
    /// `Some token` gives full reuse protection; on one that reports none, this is exactly the
    /// number-only liveness a caller would otherwise write by hand — no weaker, and never a false "dead".
    let processIsAlive (pid: int) (startTime: DateTime option) : Result<bool, ProcessError> =
        match processInfo pid with
        | Error error -> Error error
        | Ok None -> Ok false
        | Ok(Some info) ->
            match startTime, info.StartTime with
            | Some expected, Some current -> Ok(expected = current)
            | _ -> Ok true
