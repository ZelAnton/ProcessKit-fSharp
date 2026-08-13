namespace ProcessKit

open System

/// Carries a structured `ProcessError` when an error must surface through an exception.
/// Raised by `Result.GetValueOrThrow()`, streaming infrastructure (JSON-line parsing, pump faults, and
/// bounded streams in `StreamFullMode.Error` mode), and ProcessKit group initialization through DI.
/// Streaming APIs may surface pipeline faults this way even when their run-result counterparts return
/// `Result<_, ProcessError>`.
///
/// `Message` is exactly `Error.Message`, so it inherits that render's guarantees: one line, with every
/// caller-, child-, or peer-controlled fragment sanitized and bounded — safe to print into a log or a
/// terminal even when a hostile child chose its own stderr. The full, unmodified payload is on `Error`
/// (`Detail`, `Stdout`, `Stderr`, `Data`, `Original`), never truncated.
[<Sealed>]
type ProcessException internal (error: ProcessError) =
    inherit Exception(error.Message)

    /// The structured error that caused this exception.
    member _.Error = error
