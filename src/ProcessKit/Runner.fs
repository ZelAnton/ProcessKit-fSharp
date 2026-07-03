namespace ProcessKit

open System.Threading
open System.Threading.Tasks

/// The capture-derived verbs, factored out so `Runner` (over an `IProcessRunner`) and `Pipeline` (over
/// a chain) derive `run`/`runUnit`/`exitCode`/`probe`/`parse`/`tryParse` from ONE implementation
/// instead of maintaining parallel copies that could drift on trimming, success-checking, or
/// parse-error wrapping. `run`/`runUnit`/`exitCode`/`probe` take a stdout `capture` thunk (the full
/// `ProcessResult`); `parse`/`tryParse` take a `runText` thunk (the trimmed-text `run` verb) so the
/// parser is applied outside whatever retry the caller wraps the run in. The caller controls
/// invocation — e.g. `Runner` wraps the capture in the command's retry policy, `Pipeline` does not.
[<RequireQualifiedAccess>]
module internal CaptureVerbs =

    /// Require an accepted exit; return stdout with trailing whitespace trimmed.
    let run (capture: unit -> Task<Result<ProcessResult<string>, ProcessError>>) : Task<Result<string, ProcessError>> =
        task {
            match! capture () with
            | Error error -> return Error error
            | Ok result ->
                match ProcessResult.ensureSuccess result with
                | Error error -> return Error error
                | Ok ok -> return Ok(ok.Stdout.TrimEnd())
        }

    /// Like `run`, but discard the captured output.
    let runUnit capture : Task<Result<unit, ProcessError>> =
        task {
            match! run capture with
            | Error error -> return Error error
            | Ok _ -> return Ok()
        }

    /// The exit code; a signal kill or timeout errors instead of inventing a sentinel.
    let exitCode capture : Task<Result<int, ProcessError>> =
        task {
            match! capture () with
            | Error error -> return Error error
            | Ok result -> return ProcessResult.exitCode result
        }

    /// Read the exit code as a yes/no answer: 0 -> true, 1 -> false, anything else errors.
    let probe capture : Task<Result<bool, ProcessError>> =
        task {
            match! capture () with
            | Error error -> return Error error
            | Ok result -> return ProcessResult.probe result
        }

    // `parse`/`tryParse` apply a parser to the trimmed text from a `runText` thunk (the `run` verb of a
    // command or a pipeline) — NOT a raw `capture` thunk. The distinction is deliberate: the caller's
    // `runText` decides whether the *run* retries (e.g. `Runner.run` applies the command's `Retry`
    // policy), while the parse step itself is always applied exactly once, outside any retry. Re-running
    // a command because *your* parser rejected its (successfully produced) output is a different concern
    // from a flaky run, and folding the parser into the retry loop would let a custom `shouldRetry`
    // silently re-spawn the command on a parse failure.

    /// Parse the trimmed stdout from `runText` into a `'T`; a thrown parser becomes `ProcessError.Parse`.
    let parse
        (program: string)
        (parser: string -> 'T)
        (runText: unit -> Task<Result<string, ProcessError>>)
        : Task<Result<'T, ProcessError>> =
        task {
            match! runText () with
            | Error error -> return Error error
            | Ok text ->
                try
                    return Ok(parser text)
                with ex ->
                    return Error(ProcessError.Parse(program, ex.Message))
        }

    /// Apply a `Result`-returning parser to the trimmed stdout from `runText`; an `Error` message — or,
    /// like `parse`, a thrown exception's message — becomes `ProcessError.Parse`, never a faulted task.
    let tryParse
        (program: string)
        (parser: string -> Result<'T, string>)
        (runText: unit -> Task<Result<string, ProcessError>>)
        : Task<Result<'T, ProcessError>> =
        task {
            match! runText () with
            | Error error -> return Error error
            | Ok text ->
                try
                    match parser text with
                    | Ok value -> return Ok value
                    | Error message -> return Error(ProcessError.Parse(program, message))
                with ex ->
                    // A parser that throws rather than returning Error (e.g. a TryParser delegate that
                    // wraps int.Parse) is still a parse failure: surface it as a typed ProcessError.Parse.
                    return Error(ProcessError.Parse(program, ex.Message))
        }

    /// Run a *started* process to a completion result under the command's `CancelOn`-linked token: kill
    /// the tree if the token fires and report the cancellation as an error. The `start` thunk absorbs the
    /// only per-runner difference — a fresh owned group (`JobRunner`) vs. a shared group (`ProcessGroup`)
    /// — since teardown/ownership is baked into the started `RunningProcess`'s host, leaving this
    /// register → consume → cancel-map loop single-sourced for both runners.
    let runToCompletion
        (command: Command)
        (cancellationToken: CancellationToken)
        (start: unit -> Task<Result<RunningProcess, ProcessError>>)
        (consume: RunningProcess -> Task<Result<'T, ProcessError>>)
        : Task<Result<'T, ProcessError>> =
        task {
            // Honour the command's own `CancelOn` in addition to the verb's token, so a `Command.CancelOn`
            // (or a `CliClient` default) ties this run to its token through any runner.
            use linkedCts =
                match command.Config.CancelOn with
                | Some extra -> CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, extra)
                | None -> CancellationTokenSource.CreateLinkedTokenSource cancellationToken

            let effectiveToken = linkedCts.Token

            if effectiveToken.IsCancellationRequested then
                return Error(ProcessError.Cancelled command.Program)
            else
                match! start () with
                | Error error -> return Error error
                | Ok running ->
                    use _registration = effectiveToken.Register(fun () -> running.Kill())
                    let! result = consume running

                    if effectiveToken.IsCancellationRequested then
                        return Error(ProcessError.Cancelled command.Program)
                    else
                        return result
        }

/// The run verbs, expressed over any `IProcessRunner`. One verb, one meaning:
///
/// - `run` — require a zero exit; return stdout, trailing whitespace trimmed.
/// - `outputString` / `outputBytes` — the full `ProcessResult`; a non-zero exit is data.
/// - `exitCode` — the exit code; a signal kill or timeout errors instead of inventing one.
/// - `probe` — read the exit code as a yes/no: 0 -> true, 1 -> false, anything else errors.
[<RequireQualifiedAccess>]
module Runner =

    // Apply the command's `Retry` policy to a verb's final `Result`. Lives at the verb layer (not
    // the runner) so it wraps `ensureSuccess` — a non-zero exit is data to `outputString` but an
    // `Exit` error to `run`, and retry must see the latter.
    let private withRetry
        (command: Command)
        (cancellationToken: CancellationToken)
        (action: unit -> Task<Result<'T, ProcessError>>)
        : Task<Result<'T, ProcessError>> =
        match command.Config.Retry with
        | None -> action ()
        | Some(maxAttempts, delay, shouldRetry) ->
            task {
                // `maxAttempts` is the TOTAL number of runs (the initial run plus retries), so the
                // command always runs at least once: `0`/`1` (and any non-positive value) mean a single
                // run, `3` means one run and up to two retries. (Matches the vocabulary of the source
                // crate's `retry`.) Guard the `- 1` so `Int32.MinValue` can't wrap to a huge retry count.
                let maxRetries = if maxAttempts <= 1 then 0 else maxAttempts - 1
                let mutable attempt = 0
                let mutable final = None

                while final.IsNone do
                    match! action () with
                    | Ok value -> final <- Some(Ok value)
                    | Error error ->
                        // A cancelled run is always terminal: never retry a `Cancelled` error (even if a
                        // custom `shouldRetry` would), or each attempt would re-fail instantly through the
                        // still-cancelled token, burning every attempt and its backoff. Mirrors how the
                        // supervisor treats cancellation as terminal.
                        let isCancelled =
                            match error with
                            | ProcessError.Cancelled _ -> true
                            | _ -> false

                        if
                            attempt < maxRetries
                            && shouldRetry.Invoke error
                            && not cancellationToken.IsCancellationRequested
                            && not isCancelled
                        then
                            attempt <- attempt + 1
                            Log.retry command.Config.Logger command.Program attempt delay

                            try
                                // Clamp the backoff into the armable timer range so a misconfigured delay
                                // (negative / `InfiniteTimeSpan` / over-long) can't throw synchronously out
                                // of `Task.Delay` — which would break the honest-result contract — the same
                                // guard `Timeouts` applies to `Command.Timeout`.
                                do! Task.Delay(Timeouts.clampArmable delay, cancellationToken)
                            with :? System.OperationCanceledException ->
                                // Cancelled mid-backoff: don't sleep out the rest of the delay. Report it
                                // as `Cancelled` (not the prior attempt's error), consistent with every
                                // other cancellation path, so a caller matching on `Cancelled` is not
                                // misrouted into its generic-failure branch.
                                final <- Some(Error(ProcessError.Cancelled command.Program))
                        else
                            final <- Some(Error error)

                return final.Value
            }

    // Each capture verb is `withRetry` wrapped around a `CaptureVerbs` derivation of the runner's
    // `CaptureStringAsync` primitive, so retry applies uniformly and the verb logic lives in one place.
    let private captureString (runner: IProcessRunner) (cancellationToken: CancellationToken) (command: Command) =
        fun () -> runner.CaptureStringAsync(command, cancellationToken)

    /// Run to completion, capturing stdout as decoded text. A non-zero exit is data, not an error.
    let outputString (runner: IProcessRunner) (cancellationToken: CancellationToken) (command: Command) =
        withRetry command cancellationToken (fun () -> runner.CaptureStringAsync(command, cancellationToken))

    /// Run to completion, capturing stdout as raw bytes.
    let outputBytes (runner: IProcessRunner) (cancellationToken: CancellationToken) (command: Command) =
        withRetry command cancellationToken (fun () -> runner.CaptureBytesAsync(command, cancellationToken))

    /// Require a zero exit and return stdout with trailing whitespace trimmed.
    let run
        (runner: IProcessRunner)
        (cancellationToken: CancellationToken)
        (command: Command)
        : Task<Result<string, ProcessError>> =
        withRetry command cancellationToken (fun () ->
            CaptureVerbs.run (captureString runner cancellationToken command))

    /// Like `run`, but discard the captured output.
    let runUnit
        (runner: IProcessRunner)
        (cancellationToken: CancellationToken)
        (command: Command)
        : Task<Result<unit, ProcessError>> =
        withRetry command cancellationToken (fun () ->
            CaptureVerbs.runUnit (captureString runner cancellationToken command))

    /// The exit code. A signal kill or timeout errors instead of inventing a sentinel code.
    let exitCode
        (runner: IProcessRunner)
        (cancellationToken: CancellationToken)
        (command: Command)
        : Task<Result<int, ProcessError>> =
        withRetry command cancellationToken (fun () ->
            CaptureVerbs.exitCode (captureString runner cancellationToken command))

    /// Read the exit code as a yes/no answer: 0 -> true, 1 -> false, anything else errors.
    let probe
        (runner: IProcessRunner)
        (cancellationToken: CancellationToken)
        (command: Command)
        : Task<Result<bool, ProcessError>> =
        withRetry command cancellationToken (fun () ->
            CaptureVerbs.probe (captureString runner cancellationToken command))

    /// Start the command and return a live `RunningProcess` for streaming and interactive I/O.
    let start (runner: IProcessRunner) (cancellationToken: CancellationToken) (command: Command) =
        runner.SpawnAsync(command, cancellationToken)

    /// Require a zero exit and parse the trimmed stdout into a `'T`; a thrown parser error
    /// becomes `ProcessError.Parse`.
    let parse
        (runner: IProcessRunner)
        (cancellationToken: CancellationToken)
        (parser: string -> 'T)
        (command: Command)
        : Task<Result<'T, ProcessError>> =
        // The run (capture + success check) carries the retry policy; the parser is applied once,
        // outside retry — a parse failure never re-spawns the command.
        CaptureVerbs.parse command.Program parser (fun () -> run runner cancellationToken command)

    /// Like `parse`, but the parser returns its own `Result` (its error message — or a thrown
    /// exception's message — becomes `ProcessError.Parse`).
    let tryParse
        (runner: IProcessRunner)
        (cancellationToken: CancellationToken)
        (parser: string -> Result<'T, string>)
        (command: Command)
        : Task<Result<'T, ProcessError>> =
        CaptureVerbs.tryParse command.Program parser (fun () -> run runner cancellationToken command)

    /// The first stdout line satisfying `predicate`, or `None` if stdout closes without a match.
    let firstLine
        (runner: IProcessRunner)
        (cancellationToken: CancellationToken)
        (predicate: string -> bool)
        (command: Command)
        : Task<Result<string option, ProcessError>> =
        task {
            // Honour the command's `CancelOn` alongside the verb token: `firstLine` is a completion verb
            // (it runs the child to its first matching line, then reaps it), so dropping `CancelOn` here
            // would be a silent downgrade of `command.CancelOn(tok).FirstLineAsync(pred)`.
            use linkedCts =
                match command.Config.CancelOn with
                | Some extra -> CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, extra)
                | None -> CancellationTokenSource.CreateLinkedTokenSource cancellationToken

            let effectiveToken = linkedCts.Token

            match! runner.SpawnAsync(command, effectiveToken) with
            | Error error -> return Error error
            | Ok running ->
                // `use` reaps the process tree on every exit path (match below / a throwing
                // predicate / cancellation), so a streaming verb never downgrades the kill-on-drop
                // guarantee to GC finalization.
                use _ = running

                try
                    let mutable found = None
                    use enumerator = running.StdoutLinesAsync().GetAsyncEnumerator(effectiveToken)
                    let mutable more = true

                    while more do
                        let! has = enumerator.MoveNextAsync()

                        if not has then
                            more <- false
                        elif predicate enumerator.Current then
                            found <- Some enumerator.Current
                            more <- false

                    running.Kill()

                    // `firstLine` force-kills the child at the first match, so it never runs to the
                    // natural accepted-exit completion that a stdin-source failure surfaces through
                    // (`ProcessError.Stdin` fires only on an otherwise-successful run). Reporting it here
                    // would hinge on whether the child happened to exit 0 before `Kill` landed — i.e. be
                    // non-deterministic — so `firstLine` deliberately does not surface it: the matched
                    // line is the result. `FinishAsync` is still awaited to reap the tree.
                    let! _ = running.FinishAsync()
                    return Ok found
                with
                | :? System.OperationCanceledException ->
                    // Faithful to the contract: a cancelled run is always an error, not a raised
                    // OperationCanceledException.
                    return Error(ProcessError.Cancelled command.Program)
                | :? System.Threading.Channels.ChannelClosedException as ex ->
                    // A stdout-pump fault (a throwing `OnStdoutLine`/`StdoutTee` handler, or a decode/IO
                    // error) completed the channel with that exception; re-raise the ORIGINAL fault
                    // (preserving its stack) so it surfaces like `FinishAsync`/`StdoutLinesAsync`/
                    // `WaitForLineAsync`, not as a raw `ChannelClosedException` wrapper. The tree is
                    // still reaped by the `use running` above on this exceptional unwind. (A clean stdout
                    // EOF ends the loop via `not has`, so it never reaches here.)
                    let fault =
                        match ex.InnerException with
                        | null -> ex :> exn
                        | inner -> inner

                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw fault
                    return Unchecked.defaultof<_>
        }
