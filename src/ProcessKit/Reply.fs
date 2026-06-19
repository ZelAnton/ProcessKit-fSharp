namespace ProcessKit.Testing

open ProcessKit

/// A scripted reply for `ScriptedRunner`: how a matched command should appear to have run.
[<Sealed>]
type Reply private (outcome: Outcome, stdout: string, stderr: string) =

    member internal _.Outcome = outcome
    member internal _.StdoutText = stdout
    member internal _.StderrText = stderr

    /// Exit 0 with the given stdout.
    static member Ok(stdout: string) = Reply(Outcome.Exited 0, stdout, "")

    /// Exit with a non-zero code and the given stderr.
    static member Fail(code: int, stderr: string) = Reply(Outcome.Exited code, "", stderr)

    /// Exit with an explicit code (empty stdout/stderr).
    static member Exit(code: int) = Reply(Outcome.Exited code, "", "")

    /// Terminated by a signal (Unix); `None` when the signal number is unavailable.
    static member Signalled(signal: int option) = Reply(Outcome.Signalled signal, "", "")

    /// A copy of this reply with stdout replaced.
    member _.WithStdout(stdout: string) = Reply(outcome, stdout, stderr)

    /// A copy of this reply with stderr replaced.
    member _.WithStderr(stderr: string) = Reply(outcome, stdout, stderr)
