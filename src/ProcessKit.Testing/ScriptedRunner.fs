namespace ProcessKit.Testing

open System
open System.Collections.Generic
open ProcessKit

/// A subprocess-free `IProcessRunner` for tests: match commands to scripted `Reply`s.
///
/// Immutable and fluent — `On`/`When` add rules (first match wins), `Fallback` sets a
/// catch-all. A command that matches no rule and has no fallback raises, so a missing
/// stub fails the test loudly rather than silently returning a default.
///
/// Every command routed through an instance is also recorded in `Received` — a structural,
/// secret-free journal (`RecordedInvocation`) for verifying *that* the code under test made a call,
/// with `CountReceived` for the common "called exactly once" assertion — so a test needs no
/// hand-rolled counting decorator. The journal is per-instance: the fluent `On`/`When`/`Fallback`
/// return a **new** runner with its own empty journal, so record calls against the same instance you
/// pass to the code under test.
[<Sealed>]
type ScriptedRunner private (rules: ((Command -> bool) * Reply) list, fallback: Reply option) =

    // The structural call journal for this instance (thread-safe: verbs may run concurrently, and a
    // test may read `Received`/`CountReceived` while a verb is still recording). Each verb records the
    // invocation's secret-free shape on entry — before delegating to the shared seam — so a call is
    // captured even when it was pre-cancelled or matched no rule (both still count as "the double was
    // asked to run this"). Env values and stdin content are never captured (see `RecordedInvocation`).
    let received = List<RecordedInvocation>()
    let receivedGate = obj ()

    let record (command: Command) (verb: RunnerVerb) =
        let invocation = RecordedInvocation.Of(command, verb)
        lock receivedGate (fun () -> received.Add invocation)

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

            // `FakeProcess.Build` reads the command configuration, so `Command.MergeStderr()` replays as
            // one stdout stream with no separate stderr channel. PTY additionally needs `WithPty()` for
            // its recorded resize behavior.
            let fake = if command.Config.Pty.IsSome then fake.WithPty() else fake
            Ok(fake.Build())

    let seam = Seam.runner resolve

    /// An empty runner. Add rules with `On`/`When`, and a catch-all with `Fallback`.
    new() = ScriptedRunner([], None)

    /// Reply when every one of `tokens` appears in the command's program-and-arguments.
    member _.On(tokens: seq<string>, reply: Reply) =
        ArgumentNullException.ThrowIfNull(tokens, nameof tokens)
        ArgumentNullException.ThrowIfNull(reply, nameof reply)
        let wanted = List.ofSeq tokens

        let predicate (command: Command) =
            let haystack = command.Program :: List.ofSeq command.Config.Args
            wanted |> List.forall (fun token -> List.contains token haystack)

        ScriptedRunner(rules @ [ predicate, reply ], fallback)

    /// Reply when the predicate matches the command.
    member _.When(predicate: Func<Command, bool>, reply: Reply) =
        ArgumentNullException.ThrowIfNull(predicate, nameof predicate)
        ArgumentNullException.ThrowIfNull(reply, nameof reply)
        ScriptedRunner(rules @ [ predicate.Invoke, reply ], fallback)

    /// Reply to any command not matched by a rule.
    member _.Fallback(reply: Reply) =
        ArgumentNullException.ThrowIfNull(reply, nameof reply)
        ScriptedRunner(rules, Some reply)

    /// A snapshot, in call order, of every command routed through **this** runner so far — safe to read
    /// while another verb is still recording concurrently. Each entry is the secret-free shape of one
    /// invocation (program, args, cwd, env **names**, stdin/pty presence, and the verb that served it);
    /// env values and stdin content are never captured.
    member _.Received: IReadOnlyList<RecordedInvocation> =
        lock receivedGate (fun () -> received.ToArray())

    /// How many recorded invocations match `predicate` — the ergonomic verifier for
    /// "code called `git commit` exactly once" without a bespoke `IProcessRunner` decorator. Pair it
    /// with `RecordedInvocation.Matches` for token matching, e.g.
    /// `runner.CountReceived(fun inv -> inv.Matches [ "git"; "commit" ])`.
    member _.CountReceived(predicate: Func<RecordedInvocation, bool>) : int =
        ArgumentNullException.ThrowIfNull(predicate, nameof predicate)
        lock receivedGate (fun () -> received |> Seq.filter predicate.Invoke |> Seq.length)

    interface IProcessRunner with
        member _.CaptureStringAsync(command, cancellationToken) =
            record command RunnerVerb.CaptureString
            seam.CaptureStringAsync(command, cancellationToken)

        member _.SpawnAsync(command, cancellationToken) =
            record command RunnerVerb.Spawn
            seam.SpawnAsync(command, cancellationToken)

        member _.CaptureBytesAsync(command, cancellationToken) =
            record command RunnerVerb.CaptureBytes
            seam.CaptureBytesAsync(command, cancellationToken)
