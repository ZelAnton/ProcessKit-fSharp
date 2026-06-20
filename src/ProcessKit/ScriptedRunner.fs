namespace ProcessKit.Testing

open System
open System.Text
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
            let haystack = command.Program :: List.ofSeq command.Arguments
            wanted |> List.forall (fun token -> List.contains token haystack)

        ScriptedRunner(rules @ [ predicate, reply ], fallback)

    /// Reply when the predicate matches the command.
    member _.When(predicate: Command -> bool, reply: Reply) =
        ScriptedRunner(rules @ [ predicate, reply ], fallback)

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

            let result =
                ProcessResult<string>(
                    command.Program,
                    reply.StdoutText,
                    reply.StderrText,
                    reply.Outcome,
                    TimeSpan.Zero,
                    false,
                    command.Config.OkCodes
                )

            Task.FromResult(Ok result)

        member _.Start(_, _) =
            Task.FromResult(
                Error(
                    ProcessError.Unsupported
                        "ScriptedRunner does not support Start (live streaming); script OutputString/OutputBytes instead."
                )
            )

        member this.OutputBytes(command, _) =
            let reply = this.Resolve command

            let result =
                ProcessResult<byte[]>(
                    command.Program,
                    Encoding.UTF8.GetBytes reply.StdoutText,
                    reply.StderrText,
                    reply.Outcome,
                    TimeSpan.Zero,
                    false,
                    command.Config.OkCodes
                )

            Task.FromResult(Ok result)
