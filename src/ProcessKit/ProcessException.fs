namespace ProcessKit

open System

/// Raised by `Result.GetValueOrThrow()` when a run failed, carrying the structured `ProcessError`.
/// The honest-result verbs return `Result<_, ProcessError>` and never raise this — it exists only for
/// the exception-style convenience path, which reads naturally from C#
/// (`string sha = (await cmd.RunAsync()).GetValueOrThrow();`).
[<Sealed>]
type ProcessException internal (error: ProcessError) =
    inherit Exception(error.Message)

    /// The structured error that caused this exception.
    member _.Error = error
