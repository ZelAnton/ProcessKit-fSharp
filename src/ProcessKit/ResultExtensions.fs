namespace ProcessKit

open System
open System.Diagnostics.CodeAnalysis
open System.Runtime.CompilerServices
open System.Runtime.InteropServices

/// C#-idiomatic consumption of the `Result<'T, ProcessError>` that every verb returns. F# matches
/// `Ok`/`Error` directly; from C# these extensions give `Match` / `Switch` / `TryGetValue` /
/// `GetValueOrThrow` plus tuple deconstruction (`var (isOk, value, error) = result;`) without
/// touching FSharp.Core's result members. A `Result` is never `null`, and the Try/Deconstruct
/// out-parameters follow the standard .NET pattern (read `value` only when `isOk`).
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

    /// Tuple deconstruction support for C#: `var (isOk, value, error) = result;`. Inspect `isOk`
    /// before reading `value`/`error` — the inactive one is a default placeholder. For a fully
    /// null-safe match use `TryGetValue` (compiler-checked) or a positional `switch` pattern
    /// (`result switch { (true, var v, _) => …, (false, _, var e) => … }`), which binds only the
    /// matching case.
    [<Extension>]
    static member Deconstruct
        (
            result: Result<'T, ProcessError>,
            [<Out>] isOk: byref<bool>,
            [<Out>] value: byref<'T>,
            [<Out>] error: byref<ProcessError>
        ) : unit =
        match result with
        | Ok v ->
            isOk <- true
            value <- v
            error <- Unchecked.defaultof<ProcessError>
        | Error e ->
            isOk <- false
            value <- Unchecked.defaultof<'T>
            error <- e
