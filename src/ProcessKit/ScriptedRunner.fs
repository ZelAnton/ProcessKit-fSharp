namespace ProcessKit.Testing

open System
open System.Threading
open System.Threading.Tasks
open ProcessKit

/// A subprocess-free `IProcessRunner` for tests: match commands to scripted `Reply`s.
///
/// Immutable and fluent — `On`/`When` add rules (first match wins), `Fallback` sets a
/// catch-all. A command that matches no rule and has no fallback raises, so a missing
/// stub fails the test loudly rather than silently returning a default.
[<Sealed>]
type ScriptedRunner private (rules: ((Command -> bool) * Reply) list, fallback: Reply option) =

    /// An empty runner. Add rules with `On`/`When`, and a catch-all with `Fallback`.
    new() = ScriptedRunner([], None)

    /// Reply when every one of `tokens` appears in the command's program-and-arguments.
    member _.On(tokens: seq<string>, reply: Reply) =
        let wanted = List.ofSeq tokens

        let predicate (command: Command) =
            let haystack = command.Program :: command.Config.Args
            wanted |> List.forall (fun token -> List.contains token haystack)

        ScriptedRunner(rules @ [ predicate, reply ], fallback)

    /// Reply when the predicate matches the command.
    member _.When(predicate: Func<Command, bool>, reply: Reply) =
        ScriptedRunner(rules @ [ predicate.Invoke, reply ], fallback)

    /// Reply to any command not matched by a rule.
    member _.Fallback(reply: Reply) = ScriptedRunner(rules, Some reply)

    member private _.Resolve(command: Command) : Reply =
        match
            rules
            |> List.tryPick (fun (predicate, reply) -> if predicate command then Some reply else None)
        with
        | Some reply -> reply
        | None ->
            match fallback with
            | Some reply -> reply
            | None -> invalidOp $"ScriptedRunner: no scripted reply matched command '{command.Program}'."

    // Guard cancellation, resolve the matched reply, and either surface its error override or build the
    // in-memory `FakeProcess` that every seam path shares — so all three verbs agree byte-for-byte with
    // a real run (line-ending normalization, the command's encoding, `OkCodes`, output-buffer
    // truncation) and differ only in the final projection below. A cancelled run is always an error,
    // matching `JobRunner` / `ProcessGroup`, so the cancelled path is testable through the scripted seam.
    member private this.Serve
        (command: Command, cancellationToken: CancellationToken)
        : Result<RunningProcess, ProcessError> =
        if cancellationToken.IsCancellationRequested then
            Error(ProcessError.Cancelled command.Program)
        else
            let reply = this.Resolve command

            match reply.ErrorOverride with
            | Some error -> Error error
            | None ->
                Ok(
                    FakeProcess
                        .OfCommand(command)
                        .WithStdout(reply.StdoutText)
                        .WithStderr(reply.StderrText)
                        .WithOutcome(reply.Outcome)
                        .Build()
                )

    interface IProcessRunner with
        member this.CaptureStringAsync(command, cancellationToken) =
            match this.Serve(command, cancellationToken) with
            | Ok running -> running.OutputStringAsync()
            | Error error -> Task.FromResult(Error error)

        member this.SpawnAsync(command, cancellationToken) =
            Task.FromResult(this.Serve(command, cancellationToken))

        member this.CaptureBytesAsync(command, cancellationToken) =
            match this.Serve(command, cancellationToken) with
            | Ok running -> running.OutputBytesAsync()
            | Error error -> Task.FromResult(Error error)
