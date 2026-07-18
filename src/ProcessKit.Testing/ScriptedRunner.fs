namespace ProcessKit.Testing

open System
open ProcessKit

/// A subprocess-free `IProcessRunner` for tests: match commands to scripted `Reply`s.
///
/// Immutable and fluent — `On`/`When` add rules (first match wins), `Fallback` sets a
/// catch-all. A command that matches no rule and has no fallback raises, so a missing
/// stub fails the test loudly rather than silently returning a default.
[<Sealed>]
type ScriptedRunner private (rules: ((Command -> bool) * Reply) list, fallback: Reply option) =

    // Resolve the matched reply, and either surface its error override or build the in-memory
    // `FakeProcess` that every seam path shares — so all three verbs agree byte-for-byte with a real
    // run (line-ending normalization, the command's encoding, `OkCodes`, output-buffer truncation)
    // and differ only in the final projection `Seam.runner` applies. Cancellation and that projection
    // are shared with `DryRunRunner` via `Seam`, not duplicated here.
    let resolve (command: Command) : Result<RunningProcess, ProcessError> =
        let matched =
            rules
            |> List.tryPick (fun (predicate, reply) -> if predicate command then Some reply else None)

        let reply =
            match matched with
            | Some reply -> reply
            | None ->
                match fallback with
                | Some reply -> reply
                | None -> invalidOp $"ScriptedRunner: no scripted reply matched command '{command.Program}'."

        match reply.ErrorOverride with
        | Some error -> Error error
        | None ->
            let fake =
                FakeProcess
                    .OfCommand(command)
                    .WithStdout(reply.StdoutText)
                    .WithStderr(reply.StderrText)
                    .WithOutcome(reply.Outcome)

            // A scripted command that asked for a pseudo-terminal (`Command.Pty`) is served as a
            // merged-stream PTY fake (D3/D10): `OutputEventsAsync` yields only `OutputEvent.Stdout` and
            // `ResizeAsync` is a recorded no-op success — the equivalent double path for a PTY scenario.
            let fake = if command.Config.Pty.IsSome then fake.WithPty() else fake
            Ok(fake.Build())

    let seam = Seam.runner resolve

    /// An empty runner. Add rules with `On`/`When`, and a catch-all with `Fallback`.
    new() = ScriptedRunner([], None)

    /// Reply when every one of `tokens` appears in the command's program-and-arguments.
    member _.On(tokens: seq<string>, reply: Reply) =
        let wanted = List.ofSeq tokens

        let predicate (command: Command) =
            let haystack = command.Program :: List.ofSeq command.Config.Args
            wanted |> List.forall (fun token -> List.contains token haystack)

        ScriptedRunner(rules @ [ predicate, reply ], fallback)

    /// Reply when the predicate matches the command.
    member _.When(predicate: Func<Command, bool>, reply: Reply) =
        ScriptedRunner(rules @ [ predicate.Invoke, reply ], fallback)

    /// Reply to any command not matched by a rule.
    member _.Fallback(reply: Reply) = ScriptedRunner(rules, Some reply)

    interface IProcessRunner with
        member _.CaptureStringAsync(command, cancellationToken) =
            seam.CaptureStringAsync(command, cancellationToken)

        member _.SpawnAsync(command, cancellationToken) =
            seam.SpawnAsync(command, cancellationToken)

        member _.CaptureBytesAsync(command, cancellationToken) =
            seam.CaptureBytesAsync(command, cancellationToken)
