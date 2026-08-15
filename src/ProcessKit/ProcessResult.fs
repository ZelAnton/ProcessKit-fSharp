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
        okCodes: int list,
        ?configuredTimeoutDuration: TimeSpan,
        ?stdoutEncoding: Encoding,
        ?overflowTotals: int option * int option,
        ?outputDrainBounded: bool
    ) =

    // Real runners supply the configured deadline that actually fired. Results created by third-party
    // runners and test doubles cannot always identify that cause, so their honest backward-compatible
    // fallback is the actual elapsed duration rather than a fabricated configured value.
    let timeoutDuration = defaultArg configuredTimeoutDuration duration

    // The command's configured `StdoutEncoding` (`Command.StdoutEncoding`, `Encoding.UTF8` by default),
    // used to decode a `byte[]` capture in `StdoutText`. Real runners supply the command's actual
    // configured encoding; the test factories below (`Success`/`Failure`/`Create`) default to UTF-8,
    // matching `Command`'s own default so their behaviour is unchanged for existing callers.
    let stdoutEncoding = defaultArg stdoutEncoding Encoding.UTF8

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

    /// True when this capture is INCOMPLETE — a bounded `OutputBuffer` policy dropped output, or the
    /// bounded post-exit output drain cut the tail short because something that inherited the child's
    /// stdout/stderr outlived it. One flag for both, because both mean the same thing to a caller:
    /// what is here is not all of it. (A checking verb turns the two into their own typed refusals; see
    /// `ProcessResult.rejectIfTruncated`.)
    member _.Truncated = truncated

    /// The cumulative line/byte totals this run's captures SAW — retained plus dropped — carried
    /// internally so a verb that refuses a truncated capture (`ProcessResult.rejectIfTruncated`, behind
    /// `run`/`parse`/JSON) can report the real volume in its `ProcessError.OutputTooLarge` instead of a
    /// fabricated one. Each dimension has its own availability: `None` means that unit was not counted
    /// (a raw pipeline capture has no line structure, while a line pump skips its UTF-8 byte scan unless
    /// a byte cap or the fail-loud ceiling is configured); `Some 0` is an actual measured zero. Kept
    /// internal deliberately: the public signal that output was lost is `Truncated` plus the typed
    /// error, and public totals would force every producer that cannot count (test doubles, replay) to
    /// publish a zero that reads like a measurement.
    member internal _.OverflowTotals: int option * int option =
        defaultArg overflowTotals (None, None)

    /// Why this capture is incomplete, for the ONE consumer that has to tell the two sources apart:
    /// `true` when the bounded post-exit output drain cut the tail short (something that inherited the
    /// child's stdout/stderr outlived it), `false` for the ordinary source — a bounded `OutputBuffer`
    /// policy that dropped output — and for every producer that cannot distinguish them (a replayed
    /// cassette, a third-party runner), which is why the default is the buffer reading. Meaningless
    /// unless `Truncated` is set.
    ///
    /// Internal, like `OverflowTotals`, and for the same reason: the public signal that output was lost
    /// is `Truncated` plus the typed error a checking verb produces from it
    /// (`OutputTooLarge` vs. `OutputIncomplete`, see `ProcessResult.rejectIfTruncated`). A public flag
    /// would force every producer that cannot tell the sources apart to publish a `false` that reads
    /// like a measurement.
    member internal _.OutputDrainBounded: bool = defaultArg outputDrainBounded false

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

    /// Stdout as text (never null): the value itself for a `string` capture, decoded with the command's
    /// configured `StdoutEncoding` (`Encoding.UTF8` by default) for a `byte[]` capture, `ToString()` for
    /// any other captured type, `""` for a null capture. Backs the string-typed error field and the
    /// text-search / combined helpers.
    member private _.StdoutText: string =
        match box stdout with
        | :? string as s -> s
        | :? (byte[]) as bytes -> stdoutEncoding.GetString bytes
        | null -> ""
        | other -> string other

    /// The captured stdout and stderr joined into one string — stdout, then stderr on a new line when
    /// both are non-empty (for a `byte[]` stdout, decoded with the configured `StdoutEncoding`). Shares
    /// the exact join rule with
    /// `ProcessError.Combined`. This is a **post-hoc concatenation** of the two *separately* captured
    /// streams, so it does **not** reproduce their real terminal interleaving. For an honest, byte-for-byte
    /// `2>&1` view use `Command.MergeStderr`, which merges the streams at the OS level: the interleaved
    /// output then arrives on `Stdout` (and `Stderr` is empty, so `Combined` equals `Stdout`).
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
    /// reported. For a `byte[]` capture the stdout is decoded with the configured `StdoutEncoding` to
    /// fill the (string) error field.
    member internal this.FailureError: ProcessError =
        match outcome with
        | Outcome.Exited code -> ProcessError.Exit(program, code, this.StdoutText, stderr)
        | Outcome.Signalled signal -> ProcessError.Signalled(program, signal, this.StdoutText, stderr)
        | Outcome.TimedOut -> ProcessError.Timeout(program, timeoutDuration, this.StdoutText, stderr)
        | Outcome.Unobserved reason -> ProcessError.Unobserved(program, reason)

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

    /// Refuse a capture that is already TRUNCATED. The verbs that hand back stdout **as if it were the
    /// whole thing** — `run` and everything derived from it (`parse`/`tryParse`/the JSON projections, on
    /// a command or a pipeline) — call this after the success check, so an incomplete capture surfaces
    /// as a typed failure instead of feeding a caller (or a parser) a clipped prefix/tail that is
    /// indistinguishable from complete output. The lenient capture verbs
    /// (`outputString`/`outputBytes`) deliberately do NOT call it: they return the whole
    /// `ProcessResult` with `Truncated` set and let the caller decide. Ports ProcessKit-rs
    /// `ProcessResult::reject_if_truncated` (`623f2c23`).
    ///
    /// Truncation has two sources, and each gets the error that names IT — the refusal is one decision,
    /// but it is not one message:
    ///
    ///  * a bounded `OutputBuffer` policy dropped output -> `ProcessError.OutputTooLarge`, quoting the
    ///    ceilings and the totals below. This is the ordinary source, and the only one a producer that
    ///    cannot tell the two apart (a replayed cassette, a third-party runner) is ever reported as.
    ///  * the bounded post-exit output drain cut the tail short -> `ProcessError.OutputIncomplete`.
    ///    Nothing exceeded anything there: quoting a ceiling (or a line/byte/event total against one)
    ///    would name a bound that need not exist and point at a knob that cannot help.
    ///
    /// A capture both sources touched is reported as `OutputIncomplete`: the drain bound is the cause
    /// the caller does not already know about — they configured the buffer policy themselves — and it is
    /// the one that says the run's own read ends were closed on a pipe a descendant still held.
    ///
    /// `lineLimit`/`byteLimit` are the ceilings quoted in the buffer-policy error — the caller's own
    /// configured ceilings for the capture it is presenting, which is why they are passed in rather than
    /// read off the result (a pipeline captures raw bytes, so it quotes only its byte ceiling). The
    /// totals come from the result itself and are quoted only where they were actually counted (see
    /// `ProcessResult.OverflowTotals`).
    let internal rejectIfTruncated
        (lineLimit: int option)
        (byteLimit: int option)
        (result: ProcessResult<'T>)
        : Result<unit, ProcessError> =
        if not result.Truncated then
            Ok()
        elif result.OutputDrainBounded then
            Error(ProcessError.OutputIncomplete result.Program)
        else
            let totalLines, totalBytes = result.OverflowTotals

            Error(
                ProcessError.OutputTooLarge(
                    result.Program,
                    lineLimit,
                    byteLimit,
                    Option.defaultValue 0 totalLines,
                    Option.defaultValue 0 totalBytes
                )
            )

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
