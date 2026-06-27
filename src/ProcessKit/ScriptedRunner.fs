namespace ProcessKit.Testing

open System
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

    interface IProcessRunner with
        member this.OutputString(command, _) =
            let reply = this.Resolve command

            match reply.ErrorOverride with
            | Some error -> Task.FromResult(Error error)
            | None ->
                // Route capture through the same in-memory FakeProcess as `Start`, so all three seam
                // paths agree byte-for-byte with a real run: line-ending normalization, the command's
                // encoding, OkCodes, and output-buffer truncation are applied identically.
                FakeProcess
                    .OfCommand(command)
                    .WithStdout(reply.StdoutText)
                    .WithStderr(reply.StderrText)
                    .WithOutcome(reply.Outcome)
                    .Build()
                    .OutputString()

        member this.Start(command, _) =
            let reply = this.Resolve command

            match reply.ErrorOverride with
            | Some error -> Task.FromResult(Error error)
            | None ->
                // Serve a real in-memory RunningProcess so streaming/readiness consumers can be tested
                // through the same scripting as the capture verbs. OfCommand carries the matched
                // command's config (OkCodes/encodings/…) so both paths agree on success semantics.
                let running =
                    FakeProcess
                        .OfCommand(command)
                        .WithStdout(reply.StdoutText)
                        .WithStderr(reply.StderrText)
                        .WithOutcome(reply.Outcome)
                        .Build()

                Task.FromResult(Ok running)

        member this.OutputBytes(command, _) =
            let reply = this.Resolve command

            match reply.ErrorOverride with
            | Some error -> Task.FromResult(Error error)
            | None ->
                // Same FakeProcess path as OutputString/Start, so the captured bytes honour the
                // command's StdoutEncoding instead of a hardcoded UTF-8.
                FakeProcess
                    .OfCommand(command)
                    .WithStdout(reply.StdoutText)
                    .WithStderr(reply.StderrText)
                    .WithOutcome(reply.Outcome)
                    .Build()
                    .OutputBytes()
