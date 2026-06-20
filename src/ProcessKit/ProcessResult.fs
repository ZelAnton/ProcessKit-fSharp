namespace ProcessKit

open System

/// The full outcome of a run: the exit code as data, captured stdout/stderr, and timing.
///
/// A non-zero exit is **not** an error here — inspect `Code`/`IsSuccess`, or call
/// `ProcessResult.ensureSuccess` to convert a failure into a `ProcessError`. `'T` is the
/// captured-stdout type: `string` for the text verbs, `byte[]` for the bytes verbs.
[<Sealed>]
type ProcessResult<'T>
    internal
    (
        program: string,
        stdout: 'T,
        stderr: string,
        outcome: Outcome,
        duration: TimeSpan,
        truncated: bool,
        okCodes: int list
    ) =

    /// The program that was run.
    member _.Program = program

    /// The captured stdout (decoded text or raw bytes, depending on the verb).
    member _.Stdout = stdout

    /// The captured stderr, as decoded text.
    member _.Stderr = stderr

    /// How the run concluded.
    member _.Outcome = outcome

    /// Wall-clock duration of the run.
    member _.Duration = duration

    /// True when the captured output was truncated by an output-buffer policy.
    member _.Truncated = truncated

    /// The exit code, or `None` for a signal kill or timeout.
    member _.Code = outcome.Code

    /// The terminating signal, when known.
    member _.Signal = outcome.Signal

    /// True when the run was killed for exceeding its timeout.
    member _.IsTimedOut = outcome.IsTimedOut

    /// The exit codes treated as success (from `Command.OkCodes`; `{0}` by default).
    member _.AcceptedCodes = okCodes

    /// True when the process exited with an accepted code (`0`, or any code in `Command.OkCodes`).
    member _.IsSuccess =
        match outcome with
        | Outcome.Exited code -> List.contains code okCodes
        | _ -> false

[<RequireQualifiedAccess>]
module ProcessResult =

    /// Demand a successful run (an **accepted** exit code — `0`, or any in `Command.OkCodes`):
    /// returns the result unchanged on success, otherwise the corresponding `ProcessError`
    /// (`Exit` / `Signalled` / `Timeout`).
    let ensureSuccess (result: ProcessResult<string>) : Result<ProcessResult<string>, ProcessError> =
        if result.IsSuccess then
            Ok result
        else
            match result.Outcome with
            | Outcome.Exited code -> Error(ProcessError.Exit(result.Program, code, result.Stdout, result.Stderr))
            | Outcome.Signalled signal ->
                Error(ProcessError.Signalled(result.Program, signal, result.Stdout, result.Stderr))
            | Outcome.TimedOut ->
                Error(ProcessError.Timeout(result.Program, result.Duration, result.Stdout, result.Stderr))
