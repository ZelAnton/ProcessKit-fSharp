namespace ProcessKit

/// The result of finishing a streamed run: how it concluded plus its captured stderr.
///
/// Returned by `RunningProcess.Finish`, after stdout has been consumed as a stream. Sealed
/// with an internal constructor so it can gain fields without breaking the frozen API.
[<Sealed>]
type Finished internal (outcome: Outcome, stderr: string) =

    /// How the run concluded.
    member _.Outcome = outcome

    /// The captured stderr, as decoded text.
    member _.Stderr = stderr
