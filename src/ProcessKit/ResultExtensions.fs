namespace ProcessKit

open System
open System.Diagnostics.CodeAnalysis
open System.Runtime.CompilerServices
open System.Runtime.InteropServices

/// C#-idiomatic consumption of the `Result<'T, ProcessError>` that every verb returns. F# matches
/// `Ok`/`Error` directly; C# pattern-matches the result with no projection or helper call, using the
/// result's own members:
///
/// <code>
/// await cmd.OutputStringAsync() switch
/// {
///     { IsOk: true,  ResultValue: var run } => run.Stdout,
///     { IsOk: false, ErrorValue: var err } => err.Message,
/// }
/// </code>
///
/// The match is exhaustive (the `IsOk` bool covers both arms — no discard needed); `ResultValue` is
/// read only in the `IsOk: true` arm (the pattern short-circuits) and binds non-null, so no `!`. For
/// non-`switch` styles these extensions give `Match` / `Switch`, `TryGetValue`, and `GetValueOrThrow`
/// without touching the result's members. A `Result` is never `null`, and the Try out-parameters
/// follow the standard .NET pattern.
[<Extension>]
type ResultExtensions =

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
