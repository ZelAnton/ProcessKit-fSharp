namespace ProcessKit

open System

/// Structured failure type for ProcessKit operations.
///
/// Mirrors the Rust crate's `Error` enum, renamed to `ProcessError` to avoid colliding
/// with the `Result.Error` constructor in F#. Honest-result verbs — `outputString`,
/// `outputBytes`, `exitCode`, `probe` — return their value; only genuine failures
/// surface as a `ProcessError` in the `Result` channel.
[<RequireQualifiedAccess>]
type ProcessError =

    /// The process could not be spawned (a failure before or during launch).
    | Spawn of program: string * message: string

    /// The program could not be found. `searched` is the search path that was probed, when known.
    | NotFound of program: string * searched: string option

    /// A success-requiring verb (`run`) observed a non-zero exit code.
    | Exit of program: string * code: int * stdout: string * stderr: string

    /// The process was terminated by a signal (Unix) or otherwise killed without a code.
    | Signalled of program: string * signal: int option * stdout: string * stderr: string

    /// The run exceeded its configured timeout.
    | Timeout of program: string * timeout: TimeSpan * stdout: string * stderr: string

    /// The run was cancelled through its `CancellationToken`. A cancellation is always an error.
    | Cancelled of program: string

    /// A readiness probe (`WaitForLine` / `WaitForPort` / `WaitFor`) did not succeed within its timeout.
    | NotReady of program: string * timeout: TimeSpan

    /// Parsing the captured output into a typed value failed.
    | Parse of program: string * message: string

    /// Captured line output exceeded the configured `OutputBufferPolicy` ceiling
    /// (`OverflowMode.Error`). Carries the configured caps and the cumulative totals seen.
    | OutputTooLarge of
        program: string *
        lineLimit: int option *
        byteLimit: int option *
        totalLines: int *
        totalBytes: int

    /// Writing to the child's standard input failed.
    | Stdin of program: string * message: string

    /// A `ResourceLimits` cap was requested but could not be enforced — the platform has no
    /// whole-tree limit primitive (macOS / the Linux process-group fallback), or the Linux cgroup v2
    /// controllers could not be enabled (this process is not at the real cgroup root).
    | ResourceLimit of message: string

    /// An underlying I/O failure not attributable to a specific exit.
    | Io of message: string

    /// The requested operation is unsupported on this platform or in this configuration.
    | Unsupported of operation: string

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
        | ProcessError.Io message -> $"I/O error: {message}"
        | ProcessError.Unsupported operation -> $"unsupported: {operation}"

    override this.ToString() = this.Message

[<RequireQualifiedAccess>]
module ProcessError =

    /// True when the error is a program-not-found failure.
    let isNotFound (error: ProcessError) =
        match error with
        | ProcessError.NotFound _ -> true
        | _ -> false

    /// True for errors that may succeed on a retry (spawn races, transient I/O).
    let isTransient (error: ProcessError) =
        match error with
        | ProcessError.Spawn _
        | ProcessError.Io _ -> true
        | _ -> false
