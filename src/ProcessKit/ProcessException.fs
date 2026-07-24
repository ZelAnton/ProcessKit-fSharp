namespace ProcessKit

open System

/// Carries a structured `ProcessError` when an error must surface through an exception.
/// Raised by `Result.GetValueOrThrow()`, streaming infrastructure (JSON-line parsing, pump faults, and
/// bounded streams in `StreamFullMode.Error` mode), and ProcessKit group initialization through DI.
/// Streaming APIs may surface pipeline faults this way even when their run-result counterparts return
/// `Result<_, ProcessError>`.
[<Sealed>]
type ProcessException internal (error: ProcessError) =
    inherit Exception(error.Message)

    /// The structured error that caused this exception.
    member _.Error = error
