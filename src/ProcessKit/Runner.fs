namespace ProcessKit

open System.Diagnostics.CodeAnalysis
open System.Text.Json
open System.Text.Json.Serialization.Metadata
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

    /// Deserialize the trimmed stdout from `runText` as JSON into a `'T` via `System.Text.Json`
    /// (`options = None` uses the BCL defaults); invalid JSON becomes `ProcessError.Parse`, exactly like
    /// `parse` (it delegates to `parse`, so the exception-to-`ProcessError` wrapping can't drift between
    /// the two). F# records deserialize through their generated constructor by default — either give
    /// `options` with `PropertyNameCaseInsensitive = true` (STJ's constructor-based deserialization
    /// matches JSON property names to constructor parameter names case-sensitively otherwise), or mark
    /// the record `[<CLIMutable>]` for the classic default-constructor-plus-settable-properties shape.
    let outputJson<'T>
        (program: string)
        (options: JsonSerializerOptions option)
        (runText: unit -> Task<Result<string, ProcessError>>)
        : Task<Result<'T, ProcessError>> =
        // The GENERIC `JsonSerializer.Deserialize<'T>` instantiates the BCL's own `TValue?`-annotated
        // return against our ambient, unconstrained `'T` — a combination the F# nullness checker can't
        // reconcile (an unconstrained caller-supplied `'T` has no fixed nullability of its own to unify
        // against). Going through the non-generic, `Type`-based overload sidesteps that entirely: its
        // return is the concrete, ordinarily-nullable `obj`, so `'T` itself never has to carry a
        // nullability annotation, and it works uniformly whether `'T` is instantiated with a reference or
        // a value type (only reference types can ever come back null). A genuine JSON `null` raises here,
        // which `parse`'s try/with below turns into the same `ProcessError.Parse` a malformed document would.
        let deserialize (text: string) : 'T =
            let optionsArg = options |> Option.toObj

            match JsonSerializer.Deserialize(text, typeof<'T>, optionsArg) with
            | null -> raise (JsonException "the JSON document deserialized to null")
            | value -> unbox<'T> value

        parse program deserialize runText

    /// Deserialize the trimmed stdout from `runText` with caller-provided source-generated metadata.
    /// Unlike `outputJson`, this uses the non-generic `JsonTypeInfo` overload (via upcast to sidestep nullness checks) and is trim-/AOT-safe.
    let outputJsonTyped<'T>
        (program: string)
        (typeInfo: JsonTypeInfo<'T>)
        (runText: unit -> Task<Result<string, ProcessError>>)
        : Task<Result<'T, ProcessError>> =
        let deserialize (text: string) : 'T =
            match JsonSerializer.Deserialize(text, typeInfo :> JsonTypeInfo) with
            | null -> raise (JsonException "the JSON document deserialized to null")
            | value -> unbox<'T> value

        parse program deserialize runText

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
                    // A child now exists, so a one-shot stdin payload (`FromStream`/`FromLines`/
                    // `FromAsyncLines`) is spent. The real spawn boundary
                    // (`ProcessGroup.BuildHost.reserveLaunch`) has already committed it for every child
                    // ProcessKit starts itself — this is the CAPTURE boundary's own witness, for a
                    // `start` that hands back a `RunningProcess` without going through that spawn (an
                    // in-library seam or test double). Not a duplicate that decides anything on a real
                    // run: `Commit` is a single unconditional write of the same value, so the second one
                    // changes nothing; what it buys is that the retry loop can still tell "no child ever
                    // saw this payload" from "a child may already have drained it" by evidence, whichever
                    // way the attempt reached its handle. The `Error` branch above commits nothing, and
                    // is right not to: a launch that produced no child rolls its reservation back and
                    // leaves the payload intact.
                    OneShotStdin.commitLaunch command.Config.StdinSource

                    use _registration = effectiveToken.Register(fun () -> running.Kill())
                    let! result = consume running

                    if effectiveToken.IsCancellationRequested then
                        return Error(ProcessError.Cancelled command.Program)
                    else
                        return result
        }

/// The run verbs, expressed over any `IProcessRunner`. One verb, one meaning:
///
/// - `run` — require a zero/accepted exit; return stdout, trailing whitespace trimmed.
/// - `outputString` / `outputBytes` — the full `ProcessResult`; a non-zero exit is data.
/// - `exitCode` — the exit code; a signal kill or timeout errors instead of inventing one.
/// - `probe` — read the exit code as a yes/no: 0 -> true, 1 -> false, anything else errors.
[<RequireQualifiedAccess>]
module Runner =

    // Failures GUARANTEED to precede a live child, so an attempt that ends in one cannot have fed the
    // command's stdin source to anything: the program was never located (`NotFound`), the spawn itself
    // failed (`Spawn` — including a transient one that `ProcessError.isTransient` accepts), or a
    // platform/configuration primitive was refused before launch (`Unsupported`). The spawn boundary
    // starts the stdin feeder only once the spawn has succeeded, and commits the payload in the same
    // breath (`ProcessGroup.BuildHost`), so these are exactly the failures that leave a one-shot payload
    // intact — with the reservation's own `IsConsumed` verifying it independently, catching an
    // `Unsupported` a runner raises AFTER its child exists (`RunningProcess` has such a case) even
    // though the case alone reads as an up-front refusal.
    //
    // Deliberately a conservative allow-list rather than an exhaustive match: everything else — `Exit`,
    // `Timeout`, `Signalled`, `Stdin`, `OutputTooLarge`, `Unobserved`, `NotReady`, `Parse`,
    // `RetryPredicate`, `JsonRpc`, `CassetteMiss`, `ResourceLimit`, `Adopt`, `Cancelled`, and the
    // ambiguous `Io` (raised both before a child, e.g. a process group that could not be created, and
    // while driving a live one) — may have reached a child that already drained the source, so a future
    // error case is refused a one-shot retry until it is deliberately added here. Ports ProcessKit-rs
    // `is_pre_child_launch_failure` (`237c4679`).
    let private isPreChildLaunchFailure (error: ProcessError) =
        match error with
        | ProcessError.NotFound _
        | ProcessError.Spawn _
        | ProcessError.Unsupported _ -> true
        | _ -> false

    // Apply the command's `Retry` policy to a verb's final `Result`. Lives at the verb layer (not
    // the runner) so it wraps `ensureSuccess` — a non-zero exit is data to `outputString` but an
    // `Exit` error to `run`, and retry must see the latter.
    let private withRetry
        (command: Command)
        (cancellationToken: CancellationToken)
        (action: Command -> Task<Result<'T, ProcessError>>)
        : Task<Result<'T, ProcessError>> =
        // Stamp one run-level correlation id here, before the retry loop, so every attempt (and each
        // attempt's spawned incarnation) shares it — a run's spawn/exit/timeout/retry lines correlate.
        let command =
            match command.Config.RunId with
            | Some _ -> command
            | None -> command.WithRunId(Diag.newRunId ())

        // `RetryNever` is a one-shot opt-out distinct from an unset `Retry`: it wins outright, even
        // over a policy inherited from a `CliClient.WithDefaults` template, so check it first and skip
        // `Config.Retry` entirely.
        match command.Config.RetryDisabled, command.Config.Retry with
        | true, _
        | _, None -> action command
        | false, Some(maxAttempts, delayPolicy, shouldRetry) ->
            task {
                // A command-scoped `CancelOn` covers the whole retrying run, including a pending
                // backoff where no individual attempt is active.
                use retryCts =
                    match command.Config.CancelOn with
                    | Some extra -> CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, extra)
                    | None -> CancellationTokenSource.CreateLinkedTokenSource cancellationToken

                let retryToken = retryCts.Token

                // `maxAttempts` is the TOTAL number of runs (the initial run plus retries), so the
                // command always runs at least once: `0`/`1` (and any non-positive value) mean a single
                // run, `3` means one run and up to two retries. (Matches the vocabulary of the source
                // crate's `retry`.) Guard the `- 1` so `Int32.MinValue` can't wrap to a huge retry count.
                let maxRetries = if maxAttempts <= 1 then 0 else maxAttempts - 1

                // A one-shot stdin source (`FromStream`/`FromLines`/`FromAsyncLines`) feeds exactly one
                // child, so a run that may attempt the command more than once takes ownership of that
                // payload before its first attempt: a concurrent run — or a later one, once some child
                // has drained it — is refused loudly here instead of silently feeding its own child
                // empty or truncated input. A single-run policy (`maxAttempts <= 1`) reserves nothing
                // and behaves exactly as it did before, and so does every repeatable source.
                let reservationResult =
                    if maxRetries > 0 then
                        OneShotStdin.reserve command.Program command.Config.StdinSource
                    else
                        Ok None

                match reservationResult with
                | Error error -> return Error error
                | Ok reservation ->
                    // Carry this run's hold down to the boundary that actually spawns each attempt
                    // (`ProcessGroup.BuildHost`, the pipeline's stage 0), which reserves the payload for
                    // every launch it makes. Without the hold in hand it would refuse this run's own
                    // attempts as a second consumer of a payload this run itself is holding; with it, an
                    // attempt commits on a live child but never releases — that stays the run's call,
                    // made once below, so the payload is never handed to a concurrent run in the gap
                    // between two attempts. A run that reserved nothing (single attempt, or a repeatable
                    // source) drives the command exactly as it did before.
                    let command =
                        match reservation with
                        | Some reservation -> command.WithStdinReservation reservation
                        | None -> command

                    let mutable attempt = 0
                    let mutable final = None

                    // Evidence, accumulated across the run's ATTEMPTS, that the one-shot payload may
                    // already have reached a live child — the input to the hand-back decision below.
                    // The run's final error cannot answer that on its own: the retry machinery produces
                    // some finals itself (`Cancelled` from an interrupted backoff, `RetryPredicate` from
                    // a throwing classifier) long after the only attempt failed short of a child, and an
                    // attempt's own `Cancelled` is ambiguous unless its token was already cancelled when
                    // it started. Anything else an attempt does — a success, or any failure not provably
                    // pre-child — sets this, so the payload is handed back only when nothing this run did
                    // could have fed it. (T-340 R-01.)
                    let mutable payloadMayBeSpent = false

                    try
                        while final.IsNone do
                            // Sampled BEFORE the attempt: with the run's token already cancelled here, the
                            // capture launch boundary refuses to start a child at all
                            // (`CaptureVerbs.runToCompletion`, which links the same two sources this
                            // `retryToken` does), so a `Cancelled` coming back from this attempt cannot
                            // have reached one. Cancellation that arrives DURING an attempt stays
                            // ambiguous — it may have killed a live child — and keeps the payload held.
                            let cancelledBeforeAttempt = retryToken.IsCancellationRequested

                            match! action command with
                            | Ok value ->
                                payloadMayBeSpent <- true
                                final <- Some(Ok value)
                            | Error error ->
                                // A cancelled run is always terminal: never retry a `Cancelled` error (even if a
                                // custom `shouldRetry` would), or each attempt would re-fail instantly through the
                                // still-cancelled token, burning every attempt and its backoff. Mirrors how the
                                // supervisor treats cancellation as terminal.
                                let isCancelled =
                                    match error with
                                    | ProcessError.Cancelled _ -> true
                                    | _ -> false

                                // What this attempt proves about the payload. A pre-child failure leaves it
                                // untouched, and so does a `Cancelled` from an attempt whose token was
                                // already cancelled before it began (no child could be started under it).
                                // Everything else — including a cancellation that arrived mid-attempt — may
                                // have reached a child that drained the source.
                                if
                                    not (isPreChildLaunchFailure error)
                                    && not (isCancelled && cancelledBeforeAttempt)
                                then
                                    payloadMayBeSpent <- true

                                // The first attempt always runs, even for a one-shot stdin source; the gate is
                                // here, on that attempt's outcome. A second attempt is allowed only when the
                                // payload provably survived: no attempt has reached the launch boundary (the
                                // reservation is still uncommitted) AND this failure is one that precedes a live
                                // child. Any other outcome may have reached a child that already drained the
                                // source, so the run ends with this attempt's own error — no predicate (a
                                // throwing classifier would replace it with `RetryPredicate`), no backoff, and
                                // no further attempt that could only replay empty stdin or re-classify the
                                // re-consume. (T-340; ports ProcessKit-rs `237c4679`, which replaced the same
                                // blanket up-front refusal this gate supersedes.) Deliberately the strict
                                // error-class rule rather than `payloadMayBeSpent`: the one case they disagree
                                // on — a `Cancelled` under an already-cancelled token — must not reach the
                                // classifier either, since a throwing one would replace that `Cancelled` with
                                // `RetryPredicate`, and the loop refuses to retry under it anyway.
                                let oneShotStopsRetry =
                                    match reservation with
                                    | Some reservation -> reservation.IsConsumed || not (isPreChildLaunchFailure error)
                                    | None -> false

                                // Invoke the user predicate under the same typed-result boundary as parsers. A
                                // predicate fault ends this run immediately, preserving the failed attempt as
                                // `RetryPredicate.Original` instead of leaking the callback exception or starting
                                // another attempt. Keep the existing short-circuit order: when the retry budget
                                // is available, the predicate is still observed before cancellation is checked —
                                // except behind the one-shot gate above, which short-circuits to "no retry"
                                // ahead of the predicate so this attempt's own error is what the run returns.
                                let retryDecision =
                                    if oneShotStopsRetry then
                                        Ok false
                                    elif attempt < maxRetries then
                                        match error with
                                        | ProcessError.RetryPredicate _ -> Ok false
                                        | _ ->
                                            try
                                                Ok(shouldRetry.Invoke error)
                                            with ex ->
                                                // A callback fault is part of the typed retry-result contract, not a raw runner exception.
                                                Error ex
                                    else
                                        Ok false

                                match retryDecision with
                                | Error ex ->
                                    final <-
                                        Some(Error(ProcessError.RetryPredicate(command.Program, error, ex.Message)))
                                | Ok shouldRetry ->
                                    if shouldRetry && not retryToken.IsCancellationRequested && not isCancelled then
                                        attempt <- attempt + 1

                                        let delay =
                                            RetryDelayPolicy.delay attempt command.Config.RetryJitterSource delayPolicy

                                        let runId = command.Config.RunId |> Option.defaultValue ""
                                        Log.retry command.Config.Logger command.Program attempt delay runId
                                        Diag.retried command.Program

                                        try
                                            // Clamp the backoff into the armable timer range so a misconfigured delay
                                            // (negative / `InfiniteTimeSpan` / over-long) can't throw synchronously out
                                            // of `Task.Delay` — which would break the honest-result contract — the same
                                            // guard `Timeouts` applies to `Command.Timeout`.
                                            do!
                                                Task.Delay(
                                                    Timeouts.clampArmable delay,
                                                    command.Config.TimeProvider,
                                                    retryToken
                                                )
                                        with :? System.OperationCanceledException ->
                                            // Cancelled mid-backoff: don't sleep out the rest of the delay. Report it
                                            // as `Cancelled` (not the prior attempt's error), consistent with every
                                            // other cancellation path, so a caller matching on `Cancelled` is not
                                            // misrouted into its generic-failure branch.
                                            final <- Some(Error(ProcessError.Cancelled command.Program))
                                    else
                                        final <- Some(Error error)
                    finally
                        // Hand the payload back exactly when this run provably never fed it to a child: no
                        // launch boundary committed it (this run's, or a concurrent run's) and no attempt
                        // got far enough to have reached one. Decided on the ATTEMPTS' evidence, not on the
                        // run's final error, so a `Cancelled` produced by an already-cancelled token or by
                        // an interrupted backoff, and a `RetryPredicate` from a throwing classifier, all
                        // release a payload that is demonstrably intact instead of stranding it as reserved
                        // for the life of the object. Every other ending — a success, or any
                        // possibly-post-child failure — keeps it held, so no later run can be handed a
                        // source some child has already drained.
                        //
                        // In a `finally` because an attempt that throws instead of returning a typed result
                        // is the same situation: for both built-in runners the launch boundary commits
                        // before anything a caller can observe, so an uncommitted payload at that point
                        // never reached a child, and stranding it would be a worse answer than the narrow,
                        // already-documented gap for a runner that launches without committing.
                        match reservation with
                        | Some reservation when not (payloadMayBeSpent || reservation.IsConsumed) ->
                            reservation.Rollback()
                        | _ -> ()

                    match final with
                    | Some result -> return result
                    | None -> return Error(ProcessError.Io "Retry loop ended without a final result.")
            }

    // Each capture verb is `withRetry` wrapped around a `CaptureVerbs` derivation of the runner's
    // `CaptureStringAsync` primitive, so retry applies uniformly and the verb logic lives in one place.
    let private captureString (runner: IProcessRunner) (cancellationToken: CancellationToken) (command: Command) =
        fun () -> runner.CaptureStringAsync(command, cancellationToken)

    /// Run to completion, capturing stdout as decoded text. A non-zero exit is data, not an error.
    let outputString (runner: IProcessRunner) (cancellationToken: CancellationToken) (command: Command) =
        withRetry command cancellationToken (fun command -> runner.CaptureStringAsync(command, cancellationToken))

    /// Run to completion, capturing stdout as raw bytes.
    let outputBytes (runner: IProcessRunner) (cancellationToken: CancellationToken) (command: Command) =
        withRetry command cancellationToken (fun command -> runner.CaptureBytesAsync(command, cancellationToken))

    /// Require a zero/accepted exit and return stdout with trailing whitespace trimmed.
    let run
        (runner: IProcessRunner)
        (cancellationToken: CancellationToken)
        (command: Command)
        : Task<Result<string, ProcessError>> =
        withRetry command cancellationToken (fun command ->
            CaptureVerbs.run (captureString runner cancellationToken command))

    /// Like `run`, but discard the captured output.
    let runUnit
        (runner: IProcessRunner)
        (cancellationToken: CancellationToken)
        (command: Command)
        : Task<Result<unit, ProcessError>> =
        withRetry command cancellationToken (fun command ->
            CaptureVerbs.runUnit (captureString runner cancellationToken command))

    /// The exit code. A signal kill or timeout errors instead of inventing a sentinel code.
    let exitCode
        (runner: IProcessRunner)
        (cancellationToken: CancellationToken)
        (command: Command)
        : Task<Result<int, ProcessError>> =
        withRetry command cancellationToken (fun command ->
            CaptureVerbs.exitCode (captureString runner cancellationToken command))

    /// Read the exit code as a yes/no answer: 0 -> true, 1 -> false, anything else errors.
    let probe
        (runner: IProcessRunner)
        (cancellationToken: CancellationToken)
        (command: Command)
        : Task<Result<bool, ProcessError>> =
        withRetry command cancellationToken (fun command ->
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

    /// Require a zero/accepted exit and deserialize the trimmed stdout as JSON into a `'T` via
    /// `System.Text.Json` (`options = None` uses the BCL defaults); invalid JSON becomes
    /// `ProcessError.Parse`, exactly like `parse`/`tryParse` — never a raised exception. Applied once,
    /// outside any retry (see `parse`'s doc for why).
    ///
    /// **Trimming / AOT:** deserializes via reflection-based `System.Text.Json`
    /// (`JsonSerializer.Deserialize(string, Type, JsonSerializerOptions)`), so it is not trim-/AOT-safe —
    /// pass `options` with a source-generated `JsonSerializerContext`/`JsonTypeInfo<'T>` resolver, or
    /// avoid this verb, in a trimmed/NativeAOT app.
    [<RequiresUnreferencedCode "Deserializes stdout by reflection via System.Text.Json; give options a source-generated JsonSerializerContext, or avoid this verb, in a trimmed app.">]
    [<RequiresDynamicCode "Deserializes stdout by reflection via System.Text.Json; give options a source-generated JsonSerializerContext, or avoid this verb, in a NativeAOT app.">]
    let outputJson<'T>
        (runner: IProcessRunner)
        (cancellationToken: CancellationToken)
        (options: JsonSerializerOptions option)
        (command: Command)
        : Task<Result<'T, ProcessError>> =
        CaptureVerbs.outputJson command.Program options (fun () -> run runner cancellationToken command)

    /// Require a zero/accepted exit and deserialize the trimmed stdout with caller-provided
    /// source-generated `JsonTypeInfo<'T>` metadata. Invalid JSON becomes `ProcessError.Parse`, just
    /// like `outputJson`, and this overload is safe for trimmed and NativeAOT applications.
    let outputJsonTyped<'T>
        (runner: IProcessRunner)
        (cancellationToken: CancellationToken)
        (typeInfo: JsonTypeInfo<'T>)
        (command: Command)
        : Task<Result<'T, ProcessError>> =
        CaptureVerbs.outputJsonTyped command.Program typeInfo (fun () -> run runner cancellationToken command)

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
                use _registration = effectiveToken.Register(fun () -> running.Kill())

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

                    // `FinishAsync` is still awaited to reap the tree. Cancellation is checked only
                    // after that await so a cancellation in the match-to-finish window cannot be lost;
                    // a genuine finish error remains visible instead of being replaced by a match.
                    let! finished = running.FinishAsync()

                    match finished with
                    | Error error -> return Error error
                    | Ok _ when effectiveToken.IsCancellationRequested ->
                        return Error(ProcessError.Cancelled command.Program)
                    | Ok _ -> return Ok found
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
