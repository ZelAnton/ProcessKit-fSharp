namespace ProcessKit

open System
open System.Diagnostics.CodeAnalysis
open System.Runtime.CompilerServices
open System.Runtime.InteropServices

/// A C#-friendly, pattern-matchable view of the `Result<'T, ProcessError>` every verb returns: `IsOk`
/// with the success `Value`, or the `Error`. Obtain one with `result.AsRun()` (or assign a `Result`
/// straight into a `RunResult<T>` local/return — there is an implicit conversion), then `switch`
/// without positional tuples:
///
/// <code>
/// (await cmd.OutputString()).AsRun() switch
/// {
///     { IsOk: true,  Value: var run } => run.Stdout,
///     { IsOk: false, Error: var err } => err.Message,
/// }
/// </code>
///
/// The match is exhaustive — no discard arm — and `Value`/`Error` are never null (each throws if read
/// on the wrong case, which `{ IsOk: … }` short-circuits away), so neither needs a `!`. The same shape
/// reads well imperatively (`if (run.IsOk) … run.Value … else … run.Error …`). F# keeps matching
/// `Ok`/`Error` directly.
[<Struct>]
type RunResult<'T> internal (result: Result<'T, ProcessError>) =

    /// True when the verb produced a value (read `Value`); false when it failed to run (read `Error`).
    /// A non-zero/unaccepted exit is still a success here — it is data carried in `Value` (a
    /// `ProcessResult`), not a failure; only an unrun command (a `ProcessError`) is `IsOk: false`.
    member _.IsOk =
        match result with
        | Ok _ -> true
        | Error _ -> false

    /// The success value. Reading it on a failed result throws — check `IsOk` first (a `switch` on
    /// `{ IsOk: true, Value: … }` short-circuits, so the throw never fires in a well-formed match).
    /// Never null, so C# needs no null-forgiving `!`.
    member _.Value =
        match result with
        | Ok value -> value
        | Error _ -> invalidOp "RunResult.Value read on a failed result — check IsOk (or match { IsOk: true })."

    /// The failure. Reading it on a successful result throws — check `IsOk` first. Never null.
    member _.Error =
        match result with
        | Error error -> error
        | Ok _ -> invalidOp "RunResult.Error read on a successful result — check IsOk (or match { IsOk: false })."

    /// Implicit conversion so a `Result` flows into a `RunResult<T>` local or return without `AsRun()`.
    /// (C# does not apply this inside a `switch` pattern, so call `AsRun()` for an inline match.)
    static member op_Implicit(result: Result<'T, ProcessError>) : RunResult<'T> = RunResult<'T> result

/// C#-idiomatic consumption of the `Result<'T, ProcessError>` that every verb returns. F# matches
/// `Ok`/`Error` directly; from C# these give `AsRun` (a pattern-matchable `RunResult<T>`), `Match` /
/// `Switch`, `TryGetValue`, and `GetValueOrThrow` without touching FSharp.Core's result members. A
/// `Result` is never `null`, and the Try out-parameters follow the standard .NET pattern.
[<Extension>]
type ResultExtensions =

    /// A C#-friendly, pattern-matchable view of this result — `result.AsRun() switch { … }`. See
    /// `RunResult<T>`.
    [<Extension>]
    static member AsRun(result: Result<'T, ProcessError>) : RunResult<'T> = RunResult<'T> result

    /// Project both cases to a single value.
    [<Extension>]
    static member Match(result: Result<'T, ProcessError>, onOk: Func<'T, 'R>, onError: Func<ProcessError, 'R>) : 'R =
        ArgumentNullException.ThrowIfNull onOk
        ArgumentNullException.ThrowIfNull onError

        match result with
        | Ok value -> onOk.Invoke value
        | Error error -> onError.Invoke error

    /// Run the matching side effect (no return value).
    [<Extension>]
    static member Switch(result: Result<'T, ProcessError>, onOk: Action<'T>, onError: Action<ProcessError>) : unit =
        ArgumentNullException.ThrowIfNull onOk
        ArgumentNullException.ThrowIfNull onError

        match result with
        | Ok value -> onOk.Invoke value
        | Error error -> onError.Invoke error

    /// On success returns `true` and sets `value`; on failure returns `false` and sets `error`. The
    /// nullability attributes make the standard Try-pattern honest to nullable-aware C#: read `value`
    /// only when the call returned `true`, and `error` only when it returned `false`.
    [<Extension>]
    static member TryGetValue
        (
            result: Result<'T, ProcessError>,
            [<Out; MaybeNullWhen(false)>] value: byref<'T>,
            [<Out; MaybeNullWhen(true)>] error: byref<ProcessError>
        ) : bool =
        match result with
        | Ok v ->
            value <- v
            error <- Unchecked.defaultof<ProcessError>
            true
        | Error e ->
            value <- Unchecked.defaultof<'T>
            error <- e
            false

    /// The success value, or raise `ProcessException` carrying the error.
    [<Extension>]
    static member GetValueOrThrow(result: Result<'T, ProcessError>) : 'T =
        match result with
        | Ok value -> value
        | Error error -> raise (ProcessException error)
