namespace ProcessKit

/// The result of finishing a streamed run: how it concluded, its captured stderr, and whether output
/// exposed by the streaming session was truncated.
///
/// Returned by `RunningProcess.FinishAsync`, after stdout has been consumed as a stream. Sealed
/// with an internal constructor so it can gain fields without breaking the frozen API.
[<Sealed>]
type Finished internal (outcome: Outcome, stderr: string, truncated: bool) =

    /// How the run concluded.
    member _.Outcome = outcome

    /// The captured stderr, as decoded text.
    member _.Stderr = stderr

    /// Whether the stdout stream dropped items or the captured stderr was truncated.
    member _.Truncated = truncated
