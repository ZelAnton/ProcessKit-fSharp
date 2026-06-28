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
    | Spawn of Program: string * message: string

    /// The program could not be found. `searched` is the search path that was probed, when known.
    | NotFound of Program: string * Searched: string option

    /// A success-requiring verb (`run`) observed a non-zero exit code.
    | Exit of Program: string * Code: int * Stdout: string * Stderr: string

    /// The process was terminated by a signal (Unix) or otherwise killed without a code.
    | Signalled of Program: string * Signal: int option * Stdout: string * Stderr: string

    /// The run exceeded its configured timeout.
    | Timeout of Program: string * Timeout: TimeSpan * Stdout: string * Stderr: string

    /// The run was cancelled through its `CancellationToken`. A cancellation is always an error.
    | Cancelled of Program: string

    /// A readiness probe (`WaitForLineAsync` / `WaitForPortAsync` / `WaitForAsync`) did not succeed within its timeout.
    | NotReady of Program: string * Timeout: TimeSpan

    /// Parsing the captured output into a typed value failed.
    | Parse of Program: string * message: string

    /// Captured line output exceeded the configured `OutputBufferPolicy` ceiling
    /// (`OverflowMode.Error`). Carries the configured caps and the cumulative totals seen.
    | OutputTooLarge of
        Program: string *
        LineLimit: int option *
        ByteLimit: int option *
        TotalLines: int *
        TotalBytes: int

    /// Writing to the child's standard input failed.
    | Stdin of Program: string * message: string

    /// A `ResourceLimits` cap was requested but could not be enforced — the platform has no
    /// whole-tree limit primitive (macOS / the Linux process-group fallback), or the Linux cgroup v2
    /// controllers could not be enabled (this process is not at the real cgroup root).
    | ResourceLimit of message: string

    /// A `RecordReplayRunner` in replay mode found no recorded entry matching the invocation.
    | CassetteMiss of Program: string

    /// An underlying I/O failure not attributable to a specific exit.
    | Io of message: string

    /// The requested operation is unsupported on this platform or in this configuration.
    | Unsupported of Operation: string

    /// A short, human-readable description for logs and diagnostics.
    member this.Message =
        match this with
        | ProcessError.Spawn(program, message) -> $"failed to spawn '{program}': {message}"
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
        | ProcessError.Cancelled program -> $"'{program}' was cancelled"
        | ProcessError.NotReady(program, timeout) -> $"'{program}' was not ready within {timeout.TotalSeconds}s"
        | ProcessError.Parse(program, message) -> $"failed to parse output of '{program}': {message}"
        | ProcessError.OutputTooLarge(program, _, _, totalLines, totalBytes) ->
            $"'{program}' produced too much line output ({totalLines} lines / {totalBytes} bytes)"
        | ProcessError.Stdin(program, message) -> $"failed writing stdin to '{program}': {message}"
        | ProcessError.ResourceLimit message -> $"resource limit could not be enforced: {message}"
        | ProcessError.CassetteMiss program -> $"no recorded cassette entry for '{program}'"
        | ProcessError.Io message -> $"I/O error: {message}"
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
