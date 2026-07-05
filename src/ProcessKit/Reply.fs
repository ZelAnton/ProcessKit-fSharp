namespace ProcessKit.Testing

open ProcessKit

/// A scripted reply for `ScriptedRunner`: how a matched command should appear to have run. A reply
/// is either a *completed* run (any `Outcome` — surfaced as `Ok` with captured stdout/stderr) or a
/// runner-level *failure* (a `ProcessError` surfaced in the `Result` channel), so a test can script
/// the failure branch (spawn/IO/not-found), a timeout, and a non-zero exit alike.
[<Sealed>]
type Reply private (outcome: Outcome, stdout: string, stderr: string, error: ProcessError option) =

    member internal _.Outcome = outcome
    member internal _.StdoutText = stdout
    member internal _.StderrText = stderr
    /// When set, the runner returns this error instead of a completed result.
    member internal _.ErrorOverride = error

    /// Exit 0 with the given stdout.
    static member Ok(stdout: string) =
        Reply(Outcome.Exited 0, stdout, "", None)

    /// Exit with a non-zero code and the given stderr.
    static member Fail(code: int, stderr: string) =
        Reply(Outcome.Exited code, "", stderr, None)

    /// Exit with an explicit code (empty stdout/stderr).
    static member Exit(code: int) =
        Reply(Outcome.Exited code, "", "", None)

    /// Terminated by a signal (Unix) whose number is unavailable.
    static member Signalled() =
        Reply(Outcome.Signalled None, "", "", None)

    /// Terminated by signal `signalNumber` (Unix).
    static member Signalled(signalNumber: int) =
        Reply(Outcome.Signalled(Some signalNumber), "", "", None)

    /// Killed by its timeout (`Outcome.TimedOut`).
    static member TimedOut = Reply(Outcome.TimedOut, "", "", None)

    /// Concluded, but its exit status could not be observed (`Outcome.Unobserved`).
    static member Unobserved(reason: string) =
        Reply(Outcome.Unobserved reason, "", "", None)

    /// A runner-level failure: the verb returns this `ProcessError` (e.g. a spawn/IO failure or a
    /// missing program) instead of a completed result — so the error branch can be tested.
    static member Error(error: ProcessError) =
        Reply(Outcome.Exited 0, "", "", Some error)

    /// A copy of this reply with stdout replaced.
    member _.WithStdout(stdout: string) = Reply(outcome, stdout, stderr, error)

    /// A copy of this reply with stderr replaced.
    member _.WithStderr(stderr: string) = Reply(outcome, stdout, stderr, error)
