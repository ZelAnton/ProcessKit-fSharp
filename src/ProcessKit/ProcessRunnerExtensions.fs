namespace ProcessKit

open System
open System.Runtime.CompilerServices
open System.Threading

/// The full run-verb vocabulary on *any* `IProcessRunner`, derived from the three-method seam
/// (`OutputString`/`OutputBytes`/`Start`). So a chosen or injected runner — a shared `ProcessGroup`,
/// a `ScriptedRunner`, a `JobRunner` — gets `run`/`exitCode`/`probe`/`parse`/… uniformly
/// (`group.Run command`, `scripted.Probe command`), callable from F# and C#. The verb *logic* lives
/// once in the `Runner` module; these are thin sugar over it (and over the interface's own
/// `OutputString`/`OutputBytes`/`Start`, which already take a `CancellationToken`).
[<Extension>]
type ProcessRunnerExtensions =

    /// Require a zero/accepted exit and return stdout, trailing whitespace trimmed.
    [<Extension>]
    static member Run(runner: IProcessRunner, command: Command) =
        Runner.run runner CancellationToken.None command

    /// `Run`, cancellable through `cancellationToken`.
    [<Extension>]
    static member Run(runner: IProcessRunner, command: Command, cancellationToken: CancellationToken) =
        Runner.run runner cancellationToken command

    /// Require a zero/accepted exit, discarding the captured output.
    [<Extension>]
    static member RunUnit(runner: IProcessRunner, command: Command) =
        Runner.runUnit runner CancellationToken.None command

    /// `RunUnit`, cancellable through `cancellationToken`.
    [<Extension>]
    static member RunUnit(runner: IProcessRunner, command: Command, cancellationToken: CancellationToken) =
        Runner.runUnit runner cancellationToken command

    /// Run to completion, capturing stdout as decoded text (a non-zero exit is data). Forwards to the
    /// runner's own seam method, so its behaviour matches `runner.OutputString(command, ct)` exactly
    /// (no extra retry layer — retry is applied by the derived verbs and the `Command` verbs).
    [<Extension>]
    static member OutputString(runner: IProcessRunner, command: Command) =
        runner.OutputString(command, CancellationToken.None)

    /// Run to completion, capturing stdout as raw bytes (a non-zero exit is data). Forwards to the
    /// runner's own seam method, matching `runner.OutputBytes(command, ct)`.
    [<Extension>]
    static member OutputBytes(runner: IProcessRunner, command: Command) =
        runner.OutputBytes(command, CancellationToken.None)

    /// The exit code; a signal kill or timeout errors instead of inventing a sentinel.
    [<Extension>]
    static member ExitCode(runner: IProcessRunner, command: Command) =
        Runner.exitCode runner CancellationToken.None command

    /// `ExitCode`, cancellable through `cancellationToken`.
    [<Extension>]
    static member ExitCode(runner: IProcessRunner, command: Command, cancellationToken: CancellationToken) =
        Runner.exitCode runner cancellationToken command

    /// Read the exit code as a yes/no answer: 0 -> true, 1 -> false, anything else errors.
    [<Extension>]
    static member Probe(runner: IProcessRunner, command: Command) =
        Runner.probe runner CancellationToken.None command

    /// `Probe`, cancellable through `cancellationToken`.
    [<Extension>]
    static member Probe(runner: IProcessRunner, command: Command, cancellationToken: CancellationToken) =
        Runner.probe runner cancellationToken command

    /// Start the command and return a live `RunningProcess`. Forwards to the runner's own seam method.
    [<Extension>]
    static member Start(runner: IProcessRunner, command: Command) =
        runner.Start(command, CancellationToken.None)

    /// Require a zero/accepted exit and parse the trimmed stdout into a `'T`; a thrown parser error
    /// becomes `ProcessError.Parse`.
    [<Extension>]
    static member Parse(runner: IProcessRunner, command: Command, parser: Func<string, 'T>) =
        Runner.parse runner CancellationToken.None parser.Invoke command

    /// `Parse`, cancellable through `cancellationToken`.
    [<Extension>]
    static member Parse
        (runner: IProcessRunner, command: Command, parser: Func<string, 'T>, cancellationToken: CancellationToken)
        =
        Runner.parse runner cancellationToken parser.Invoke command

    /// Like `Parse`, but the parser returns its own `Result` (its error becomes `Parse`).
    [<Extension>]
    static member TryParse(runner: IProcessRunner, command: Command, parser: Func<string, Result<'T, string>>) =
        Runner.tryParse runner CancellationToken.None parser.Invoke command

    /// `TryParse`, cancellable through `cancellationToken`.
    [<Extension>]
    static member TryParse
        (
            runner: IProcessRunner,
            command: Command,
            parser: Func<string, Result<'T, string>>,
            cancellationToken: CancellationToken
        ) =
        Runner.tryParse runner cancellationToken parser.Invoke command

    /// The first stdout line satisfying `predicate`, or `None` if stdout closes without a match.
    [<Extension>]
    static member FirstLine(runner: IProcessRunner, command: Command, predicate: Func<string, bool>) =
        Runner.firstLine runner CancellationToken.None predicate.Invoke command

    /// `FirstLine`, cancellable through `cancellationToken`.
    [<Extension>]
    static member FirstLine
        (runner: IProcessRunner, command: Command, predicate: Func<string, bool>, cancellationToken: CancellationToken)
        =
        Runner.firstLine runner cancellationToken predicate.Invoke command
