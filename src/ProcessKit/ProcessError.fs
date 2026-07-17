namespace ProcessKit

open System

/// Structured failure type for ProcessKit operations.
///
/// Named `ProcessError` (rather than just `Error`) to avoid colliding with the `Result.Error`
/// constructor in F#. Honest-result verbs — `outputString`, `outputBytes`, `exitCode`, `probe` —
/// return their value; only genuine failures surface as a `ProcessError` in the `Result` channel.
[<RequireQualifiedAccess; NoComparison>]
type ProcessError =

    /// The process could not be spawned (a failure before or during launch).
    | Spawn of Program: string * Detail: string

    /// The program could not be found. `Searched` is the search path that was probed, when known.
    | NotFound of Program: string * Searched: string option

    /// A success-requiring verb (`run`) observed a non-zero exit code.
    | Exit of Program: string * Code: int * Stdout: string * Stderr: string

    /// The process was terminated by a signal (Unix) or otherwise killed without a code.
    | Signalled of Program: string * Signal: int option * Stdout: string * Stderr: string

    /// The run exceeded its configured timeout.
    | Timeout of Program: string * Timeout: TimeSpan * Stdout: string * Stderr: string

    /// The process concluded but its actual exit status could not be observed (see
    /// `Outcome.Unobserved`) — a native API failure or an unresolved POSIX reap race. `Detail` carries
    /// the reason. Always a failure; never fabricated as a clean exit.
    | Unobserved of Program: string * Detail: string

    /// The run was cancelled through its `CancellationToken`. A cancellation is always an error.
    | Cancelled of Program: string

    /// A readiness probe (`WaitForLineAsync` / `WaitForPortAsync` / `WaitForHttpAsync` / `WaitForAsync`) did not succeed within its timeout.
    | NotReady of Program: string * Timeout: TimeSpan

    /// Parsing the captured output into a typed value failed.
    | Parse of Program: string * Detail: string

    /// Captured line output exceeded the configured `OutputBufferPolicy` ceiling
    /// (`OverflowMode.Error`). Carries the configured caps and the cumulative totals seen.
    | OutputTooLarge of
        Program: string *
        LineLimit: int option *
        ByteLimit: int option *
        TotalLines: int *
        TotalBytes: int

    /// The child's stdin source could not be read (e.g. a missing `FromFile` path) on an otherwise-
    /// successful run. A routine broken pipe — the child closed stdin early — is never reported here.
    | Stdin of Program: string * Detail: string

    /// A `ResourceLimits` cap was requested but could not be enforced — the platform has no
    /// whole-tree limit primitive (macOS / the Linux process-group fallback), or the Linux cgroup v2
    /// controllers could not be enabled (this process is not at the real cgroup root).
    | ResourceLimit of Detail: string

    /// A `RecordReplayRunner` in replay mode found no recorded entry matching the invocation.
    | CassetteMiss of Program: string

    /// An underlying I/O failure not attributable to a specific exit.
    | Io of Detail: string

    /// The requested operation is unsupported on this platform or in this configuration.
    | Unsupported of Operation: string

    /// A short, human-readable description for logs and diagnostics.
    member this.Message =
        match this with
        | ProcessError.Spawn(program, detail) -> $"failed to spawn '{program}': {detail}"
        | ProcessError.NotFound(program, searched) ->
            match searched with
            | Some path -> $"program '{program}' was not found (searched {path})"
            | None -> $"program '{program}' was not found"
        | ProcessError.Exit(program, code, _, stderr) ->
            if System.String.IsNullOrEmpty stderr then
                $"'{program}' exited with code {code}"
            else
                $"'{program}' exited with code {code}: {stderr.Trim()}"
        | ProcessError.Signalled(program, signal, _, _) ->
            match signal with
            | Some s -> $"'{program}' was terminated by signal {s}"
            | None -> $"'{program}' was killed"
        | ProcessError.Timeout(program, timeout, _, _) -> $"'{program}' timed out after {timeout.TotalSeconds}s"
        | ProcessError.Unobserved(program, detail) -> $"'{program}' concluded, but its exit status is unknown: {detail}"
        | ProcessError.Cancelled program -> $"'{program}' was cancelled"
        | ProcessError.NotReady(program, timeout) -> $"'{program}' was not ready within {timeout.TotalSeconds}s"
        | ProcessError.Parse(program, detail) -> $"failed to parse output of '{program}': {detail}"
        | ProcessError.OutputTooLarge(program, _, _, totalLines, totalBytes) ->
            $"'{program}' produced too much line output ({totalLines} lines / {totalBytes} bytes)"
        | ProcessError.Stdin(program, detail) -> $"could not read the stdin source for '{program}': {detail}"
        | ProcessError.ResourceLimit detail -> $"resource limit could not be enforced: {detail}"
        | ProcessError.CassetteMiss program -> $"no recorded cassette entry for '{program}'"
        | ProcessError.Io detail -> $"I/O error: {detail}"
        | ProcessError.Unsupported operation -> $"unsupported: {operation}"

    override this.ToString() = this.Message

    /// True for errors that may succeed on a retry (a spawn race or transient I/O). The instance
    /// form of `ProcessError.isTransient`, so it reads cleanly from C# as `err.IsTransient` (the
    /// not-found classifier already has the generated `err.IsNotFound` tester).
    member this.IsTransient =
        match this with
        | ProcessError.Spawn _
        | ProcessError.Io _ -> true
        | _ -> false

    // The read-without-destructure accessors below let a consumer read a failure's fields off the base
    // `ProcessError` without pattern-matching each case — the only practical way to do it from C#, which
    // can't destructure an F# union. Each returns the field for the cases that carry it, `None` otherwise.

    /// The program the error is about, when it carries one — `None` for `ResourceLimit` / `Io` /
    /// `Unsupported`, which are not tied to a specific program.
    member this.Program: string option =
        match this with
        | ProcessError.Spawn(program, _)
        | ProcessError.NotFound(program, _)
        | ProcessError.Exit(program, _, _, _)
        | ProcessError.Signalled(program, _, _, _)
        | ProcessError.Timeout(program, _, _, _)
        | ProcessError.Unobserved(program, _)
        | ProcessError.Cancelled program
        | ProcessError.NotReady(program, _)
        | ProcessError.Parse(program, _)
        | ProcessError.OutputTooLarge(program, _, _, _, _)
        | ProcessError.Stdin(program, _)
        | ProcessError.CassetteMiss program -> Some program
        | ProcessError.ResourceLimit _
        | ProcessError.Io _
        | ProcessError.Unsupported _ -> None

    /// The captured stdout when the error carries it (`Exit` / `Signalled` / `Timeout`); `None` otherwise.
    member this.Stdout: string option =
        match this with
        | ProcessError.Exit(_, _, stdout, _)
        | ProcessError.Signalled(_, _, stdout, _)
        | ProcessError.Timeout(_, _, stdout, _) -> Some stdout
        | _ -> None

    /// The captured stderr when the error carries it (`Exit` / `Signalled` / `Timeout`); `None` otherwise.
    member this.Stderr: string option =
        match this with
        | ProcessError.Exit(_, _, _, stderr)
        | ProcessError.Signalled(_, _, _, stderr)
        | ProcessError.Timeout(_, _, _, stderr) -> Some stderr
        | _ -> None

    /// The captured stdout and stderr joined (stdout, then stderr on a new line when both are non-empty)
    /// for the stream-carrying cases (`Exit` / `Signalled` / `Timeout`); `None` otherwise.
    member this.Combined: string option =
        match this with
        | ProcessError.Exit(_, _, stdout, stderr)
        | ProcessError.Signalled(_, _, stdout, stderr)
        | ProcessError.Timeout(_, _, stdout, stderr) -> Some(ProcessError.CombineStreams(stdout, stderr))
        | _ -> None

    /// The exit code when the error is an `Exit`; `None` otherwise (a signal kill or timeout has none).
    member this.Code: int option =
        match this with
        | ProcessError.Exit(_, code, _, _) -> Some code
        | _ -> None

    /// The terminating signal number when the error is a `Signalled` with a known number; `None` otherwise.
    member this.Signal: int option =
        match this with
        | ProcessError.Signalled(_, signal, _, _) -> signal
        | _ -> None

    /// The shared combined-output join: both streams non-empty → `stdout` + newline + `stderr`; else the
    /// non-empty one (or `""` when both are empty). One rule for `ProcessError.Combined` and
    /// `ProcessResult.Combined`, so the two views can't drift. Internal.
    static member internal CombineStreams(stdout: string, stderr: string) : string =
        match String.IsNullOrEmpty stdout, String.IsNullOrEmpty stderr with
        | false, false -> stdout + "\n" + stderr
        | false, true -> stdout
        | true, false -> stderr
        | true, true -> ""

[<RequireQualifiedAccess>]
module ProcessError =

    /// True when the error is a program-not-found failure.
    let isNotFound (error: ProcessError) =
        match error with
        | ProcessError.NotFound _ -> true
        | _ -> false

    /// True for errors that may succeed on a retry (spawn races, transient I/O). Delegates to the
    /// instance `ProcessError.IsTransient` so the two never drift.
    let isTransient (error: ProcessError) = error.IsTransient
