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

    /// An underlying I/O failure not attributable to a specific exit.
    | Io of message: string

    /// The requested operation is unsupported on this platform or in this configuration.
    | Unsupported of operation: string

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
