namespace ProcessKit

open System.Threading
open System.Threading.Tasks

/// The run verbs, expressed over any `IProcessRunner` (the F# analog of the Rust
/// `ProcessRunnerExt` trait). One verb, one meaning:
///
/// - `run` — require a zero exit; return stdout, trailing whitespace trimmed.
/// - `outputString` / `outputBytes` — the full `ProcessResult`; a non-zero exit is data.
/// - `exitCode` — the exit code; a signal kill or timeout errors instead of inventing one.
/// - `probe` — read the exit code as a yes/no: 0 -> true, 1 -> false, anything else errors.
[<RequireQualifiedAccess>]
module Runner =

    /// Run to completion, capturing stdout as decoded text. A non-zero exit is data, not an error.
    let outputString (runner: IProcessRunner) (cancellationToken: CancellationToken) (command: Command) =
        runner.OutputString(command, cancellationToken)

    /// Run to completion, capturing stdout as raw bytes.
    let outputBytes (runner: IProcessRunner) (cancellationToken: CancellationToken) (command: Command) =
        runner.OutputBytes(command, cancellationToken)

    /// Require a zero exit and return stdout with trailing whitespace trimmed.
    let run
        (runner: IProcessRunner)
        (cancellationToken: CancellationToken)
        (command: Command)
        : Task<Result<string, ProcessError>> =
        task {
            match! runner.OutputString(command, cancellationToken) with
            | Error error -> return Error error
            | Ok result ->
                match ProcessResult.ensureSuccess result with
                | Error error -> return Error error
                | Ok ok -> return Ok(ok.Stdout.TrimEnd())
        }

    /// Like `run`, but discard the captured output.
    let runUnit
        (runner: IProcessRunner)
        (cancellationToken: CancellationToken)
        (command: Command)
        : Task<Result<unit, ProcessError>> =
        task {
            match! run runner cancellationToken command with
            | Error error -> return Error error
            | Ok _ -> return Ok()
        }

    /// The exit code. A signal kill or timeout errors instead of inventing a sentinel code.
    let exitCode
        (runner: IProcessRunner)
        (cancellationToken: CancellationToken)
        (command: Command)
        : Task<Result<int, ProcessError>> =
        task {
            match! runner.OutputString(command, cancellationToken) with
            | Error error -> return Error error
            | Ok result ->
                match result.Outcome with
                | Outcome.Exited code -> return Ok code
                | Outcome.Signalled signal ->
                    return Error(ProcessError.Signalled(result.Program, signal, result.Stdout, result.Stderr))
                | Outcome.TimedOut ->
                    return Error(ProcessError.Timeout(result.Program, result.Duration, result.Stdout, result.Stderr))
        }

    /// Read the exit code as a yes/no answer: 0 -> true, 1 -> false, anything else errors.
    let probe
        (runner: IProcessRunner)
        (cancellationToken: CancellationToken)
        (command: Command)
        : Task<Result<bool, ProcessError>> =
        task {
            match! runner.OutputString(command, cancellationToken) with
            | Error error -> return Error error
            | Ok result ->
                match result.Outcome with
                | Outcome.Exited 0 -> return Ok true
                | Outcome.Exited 1 -> return Ok false
                | Outcome.Exited code ->
                    return Error(ProcessError.Exit(result.Program, code, result.Stdout, result.Stderr))
                | Outcome.Signalled signal ->
                    return Error(ProcessError.Signalled(result.Program, signal, result.Stdout, result.Stderr))
                | Outcome.TimedOut ->
                    return Error(ProcessError.Timeout(result.Program, result.Duration, result.Stdout, result.Stderr))
        }
