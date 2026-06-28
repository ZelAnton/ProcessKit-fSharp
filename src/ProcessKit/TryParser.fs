namespace ProcessKit

open System.Diagnostics.CodeAnalysis
open System.Runtime.InteropServices

/// A C#-friendly parser for the `TryParse` verbs: the standard .NET `bool TryX(string, out T)` shape,
/// so C# can pass a BCL parser like `int.TryParse` / `DateTime.TryParse`. Give the verb an explicit
/// type argument — `cmd.TryParseAsync&lt;int&gt;(int.TryParse)` — or assign the parser to a
/// `TryParser&lt;int&gt;` first; the explicit `'T` is required because BCL `TryParse` methods are
/// overloaded (and C# can't infer `'T` from a byref lambda parameter either). It returns `true` and
/// sets `value` on success, `false` otherwise — a `false` result becomes `ProcessError.Parse`. For a
/// custom error *message*, throw from a `Parse` parser, or use the `Result`-returning `Runner.tryParse`
/// from F#.
type TryParser<'T> = delegate of input: string * [<Out; MaybeNullWhen(false)>] value: byref<'T> -> bool

/// Adapter from a C#-friendly `TryParser<'T>` to the `Result`-returning parser the `Runner` verbs use.
[<RequireQualifiedAccess>]
module internal TryParser =

    /// A `false` return becomes a `Parse` error (no custom message — the delegate carries none).
    let toResult (parser: TryParser<'T>) : string -> Result<'T, string> =
        fun input ->
            match parser.Invoke input with
            | true, value -> Ok value
            | false, _ -> Error "the parser rejected the output"
