namespace ProcessKit.Testing

open System.Collections.Generic
open ProcessKit

/// Which `IProcessRunner` primitive served a recorded invocation through a test double — the seam the
/// code under test ultimately routed through (each verb of the public vocabulary reduces to one of
/// these three).
type RunnerVerb =
    /// `CaptureStringAsync` — behind the text-capture verbs (`OutputStringAsync`/`RunAsync`/`ExitCodeAsync`/…).
    | CaptureString
    /// `CaptureBytesAsync` — behind the raw-bytes verb (`OutputBytesAsync`).
    | CaptureBytes
    /// `SpawnAsync` — behind the live-handle verbs (`StartAsync`/`firstLine`).
    | Spawn

/// A structural, secret-free record of one command run through a test double (`ScriptedRunner.Received`),
/// captured for after-the-fact verification — "was `git` called with these arguments exactly once?" —
/// without hand-rolling an `IProcessRunner` decorator.
///
/// **Secret invariant.** Only the *shape* of a command is recorded, never its secret payloads:
/// environment-variable **names** (`EnvNames`) but never their values, and only *whether* stdin was
/// present (`HasStdin`) — never its content. This mirrors the `RecordReplayRunner` cassette contract
/// (`Cassette.fs`), so a test double never becomes a place secrets quietly accumulate.
[<Sealed>]
type RecordedInvocation
    internal
    (
        program: string,
        args: IReadOnlyList<string>,
        arg0: string option,
        cwd: string option,
        envNames: IReadOnlyList<string>,
        hasStdin: bool,
        pty: bool,
        verb: RunnerVerb
    ) =

    /// The command's program — the executable that is launched and drives PATH/`PreferLocal` resolution.
    member _.Program = program

    /// The command's arguments, in order (never including the program itself).
    member _.Args = args

    /// The `Command.Arg0` override of the child's `argv[0]`, when set — `None` for the ordinary case
    /// where the child observes `Program` itself as `argv[0]`.
    member _.Arg0 = arg0

    /// The command's working directory, or `None` when it inherited the parent's.
    member _.Cwd = cwd

    /// The **names** of the environment variables the command overrode — sorted and de-duplicated.
    /// Values are deliberately never captured (the secret invariant); a name still tells you *that* a
    /// variable was set.
    member _.EnvNames = envNames

    /// Whether the command supplied stdin — the *fact* only. The content is never captured.
    member _.HasStdin = hasStdin

    /// Whether the command requested a pseudo-terminal (`Command.Pty`).
    member _.Pty = pty

    /// Which `IProcessRunner` primitive served this invocation.
    member _.Verb = verb

    /// `true` when every token in `tokens` appears among the program and its arguments (an
    /// order-independent subset test) — the same matching `ScriptedRunner.On` uses, so a call scripted
    /// with `On [ "git"; "commit" ]` verifies with `inv.Matches [ "git"; "commit" ]`.
    member _.Matches(tokens: seq<string>) : bool =
        let haystack = program :: List.ofSeq args
        tokens |> Seq.forall (fun token -> List.contains token haystack)

    /// Build a `RecordedInvocation` from a command and the verb that served it, capturing only the
    /// secret-free shape — the single extraction point, whose env-name projection mirrors `Cassette.fs`
    /// (`envNamesOf`) so both doubles agree on what "names only" means.
    static member internal Of(command: Command, verb: RunnerVerb) : RecordedInvocation =
        let envNames =
            command.Config.EnvOverrides
            |> Seq.map fst
            |> Seq.distinct
            |> Seq.sort
            |> Seq.toArray
            :> IReadOnlyList<string>

        RecordedInvocation(
            command.Program,
            command.Arguments,
            command.Config.Arg0,
            command.WorkingDirectory,
            envNames,
            command.Config.StdinSource.IsSome,
            command.Config.Pty.IsSome,
            verb
        )
