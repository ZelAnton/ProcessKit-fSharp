namespace ProcessKit

open System
open System.Collections.Generic
open System.Text

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
    member _.AcceptedCodes: IReadOnlyList<int> = List.toArray okCodes

    /// True when the process exited with one of the accepted codes (`Command.OkCodes`; `{0}` by default).
    member _.IsSuccess = outcome.IsAcceptedBy okCodes

    /// Stdout as text (never null): the value itself for a `string` capture, UTF-8-decoded for a
    /// `byte[]` capture, `ToString()` for any other captured type, `""` for a null capture. Backs the
    /// string-typed error field and the text-search / combined helpers.
    member private _.StdoutText: string =
        match box stdout with
        | :? string as s -> s
        | :? (byte[]) as bytes -> Encoding.UTF8.GetString bytes
        | null -> ""
        | other -> string other

    /// The captured stdout and stderr joined into one string — stdout, then stderr on a new line when
    /// both are non-empty (for a `byte[]` stdout, UTF-8-decoded). Shares the exact join rule with
    /// `ProcessError.Combined`.
    member this.Combined: string = ProcessError.CombineStreams(this.StdoutText, stderr)

    /// True when any of `needles` appears (case-insensitive, ordinal) in either captured stream — the
    /// "a specific non-zero exit is benign when a known stdout/stderr marker is present" idiom (e.g. a
    /// tool exiting 1 with `no changes` in its output). Each stream is searched independently, so a
    /// needle never matches across the stdout/stderr boundary; a null needle is skipped and an empty
    /// `needles` is `false`.
    member this.OutputContainsAny(needles: seq<string>) : bool =
        ArgumentNullException.ThrowIfNull needles
        let stdoutText = this.StdoutText
        // `String.IsNullOrEmpty` / `box` guard against a null stderr or a null needle element that a
        // non-nullable-unaware C# caller can still pass (the F# types say non-null); a null needle is
        // skipped, an empty needle matches (String.Contains parity).
        let stderrText = if String.IsNullOrEmpty stderr then "" else stderr

        needles
        |> Seq.exists (fun needle ->
            not (isNull (box needle))
            && (stdoutText.Contains(needle, StringComparison.OrdinalIgnoreCase)
                || stderrText.Contains(needle, StringComparison.OrdinalIgnoreCase)))

    /// The single mapping from a non-success outcome to its `ProcessError`. Lives on the type so the
    /// instance `EnsureSuccess` and the module verbs (`ensureSuccess` / `exitCode` / `probe`, on a
    /// command or a pipeline) can never drift on how a non-zero exit, signal kill, or timeout is
    /// reported. For a `byte[]` capture the stdout is decoded UTF-8 to fill the (string) error field.
    member internal this.FailureError: ProcessError =
        match outcome with
        | Outcome.Exited code -> ProcessError.Exit(program, code, this.StdoutText, stderr)
        | Outcome.Signalled signal -> ProcessError.Signalled(program, signal, this.StdoutText, stderr)
        | Outcome.TimedOut -> ProcessError.Timeout(program, duration, this.StdoutText, stderr)

    /// Demand a successful run (an **accepted** exit code — one in `Command.OkCodes`, `{0}` by default):
    /// returns the result unchanged on success, otherwise the corresponding `ProcessError`
    /// (`Exit` / `Signalled` / `Timeout`). The instance form for C# fluency.
    member this.EnsureSuccess() : Result<ProcessResult<'T>, ProcessError> =
        if this.IsSuccess then Ok this else Error this.FailureError

[<RequireQualifiedAccess>]
module ProcessResult =

    /// The single mapping from a non-success outcome to its `ProcessError` (delegates to the type's
    /// `FailureError` so there is exactly one source of truth).
    let internal failureError (result: ProcessResult<'T>) : ProcessError = result.FailureError

    /// Demand a successful run (an **accepted** exit code — one in `Command.OkCodes`, `{0}` by default):
    /// returns the result unchanged on success, otherwise the corresponding `ProcessError`
    /// (`Exit` / `Signalled` / `Timeout`). Generic over the captured-stdout type.
    let ensureSuccess (result: ProcessResult<'T>) : Result<ProcessResult<'T>, ProcessError> = result.EnsureSuccess()

    // Test factories: build a `ProcessResult<'T>` directly (no real process), to unit-test code that
    // consumes one. Generic over the captured-stdout type, so C# infers it (`ProcessResult.Success("x")`,
    // no type argument) and F# does too (`ProcessResult.Success "x"`). The program name is empty.

    /// Build a successful `ProcessResult` (exit 0, given stdout) for tests.
    let Success (stdout: 'T) : ProcessResult<'T> =
        ProcessResult<'T>("", stdout, "", Outcome.Exited 0, TimeSpan.Zero, false, [ 0 ])

    /// Build a failed `ProcessResult` (a non-zero exit) for tests. `exitCode` must be non-zero — `0`
    /// is rejected because the result is judged against the default `{0}` ok-codes, so a zero exit
    /// would make `IsSuccess` `true` (use `Success` for that). Negative codes are allowed (Windows
    /// reports them).
    let Failure (stdout: 'T) (stderr: string) (exitCode: int) : ProcessResult<'T> =
        ArgumentOutOfRangeException.ThrowIfZero exitCode
        ProcessResult<'T>("", stdout, stderr, Outcome.Exited exitCode, TimeSpan.Zero, false, [ 0 ])

    /// Build a `ProcessResult` with full control over stderr, the `Outcome` (e.g. `Outcome.Signalled` /
    /// `Outcome.TimedOut`), and the duration. Success is judged against the default `{0}` ok-codes.
    let Create (stdout: 'T) (stderr: string) (outcome: Outcome) (duration: TimeSpan) : ProcessResult<'T> =
        ProcessResult<'T>("", stdout, stderr, outcome, duration, false, [ 0 ])

    /// The exit code; a signal kill or timeout errors instead of inventing a sentinel.
    let internal exitCode (result: ProcessResult<string>) : Result<int, ProcessError> =
        match result.Outcome with
        | Outcome.Exited code -> Ok code
        | _ -> Error result.FailureError

    /// Read the exit code as a yes/no answer: 0 -> true, 1 -> false, anything else errors.
    let internal probe (result: ProcessResult<string>) : Result<bool, ProcessError> =
        match result.Outcome with
        | Outcome.Exited 0 -> Ok true
        | Outcome.Exited 1 -> Ok false
        | _ -> Error result.FailureError
