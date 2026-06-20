namespace ProcessKit

open System.Runtime.CompilerServices
open System.Threading

/// Default-runner convenience verbs on `Command`, callable from F# and C# as
/// `command.Start()` / `command.Run()` etc. They use a shared `JobRunner`; for a custom or
/// injected runner, go through `Runner.*` or call the runner directly.
[<Extension>]
type CommandVerbs =

    static member val internal DefaultRunner: IProcessRunner = JobRunner()

    /// Start the command and return a live `RunningProcess`.
    [<Extension>]
    static member Start(command: Command) =
        CommandVerbs.DefaultRunner.Start(command, CancellationToken.None)

    /// Start the command, cancellable through `cancellationToken`.
    [<Extension>]
    static member Start(command: Command, cancellationToken: CancellationToken) =
        CommandVerbs.DefaultRunner.Start(command, cancellationToken)

    /// Require a zero exit and return stdout, trailing whitespace trimmed.
    [<Extension>]
    static member Run(command: Command) =
        Runner.run CommandVerbs.DefaultRunner CancellationToken.None command

    /// Require a zero exit and return stdout, cancellable through `cancellationToken`.
    [<Extension>]
    static member Run(command: Command, cancellationToken: CancellationToken) =
        Runner.run CommandVerbs.DefaultRunner cancellationToken command

    /// Require a zero exit, discarding the captured output.
    [<Extension>]
    static member RunUnit(command: Command) =
        Runner.runUnit CommandVerbs.DefaultRunner CancellationToken.None command

    /// Require a zero exit (discarding output), cancellable through `cancellationToken`.
    [<Extension>]
    static member RunUnit(command: Command, cancellationToken: CancellationToken) =
        Runner.runUnit CommandVerbs.DefaultRunner cancellationToken command

    /// Run to completion, capturing stdout as decoded text (a non-zero exit is data).
    [<Extension>]
    static member OutputString(command: Command) =
        Runner.outputString CommandVerbs.DefaultRunner CancellationToken.None command

    /// Run to completion capturing stdout as text, cancellable through `cancellationToken`.
    [<Extension>]
    static member OutputString(command: Command, cancellationToken: CancellationToken) =
        Runner.outputString CommandVerbs.DefaultRunner cancellationToken command

    /// Run to completion, capturing stdout as raw bytes.
    [<Extension>]
    static member OutputBytes(command: Command) =
        Runner.outputBytes CommandVerbs.DefaultRunner CancellationToken.None command

    /// Run to completion capturing stdout as raw bytes, cancellable through `cancellationToken`.
    [<Extension>]
    static member OutputBytes(command: Command, cancellationToken: CancellationToken) =
        Runner.outputBytes CommandVerbs.DefaultRunner cancellationToken command

    /// The exit code; a signal kill or timeout errors instead of inventing a sentinel code.
    [<Extension>]
    static member ExitCode(command: Command) =
        Runner.exitCode CommandVerbs.DefaultRunner CancellationToken.None command

    /// The exit code, cancellable through `cancellationToken`.
    [<Extension>]
    static member ExitCode(command: Command, cancellationToken: CancellationToken) =
        Runner.exitCode CommandVerbs.DefaultRunner cancellationToken command

    /// Read the exit code as a yes/no answer: 0 -> true, 1 -> false, anything else errors.
    [<Extension>]
    static member Probe(command: Command) =
        Runner.probe CommandVerbs.DefaultRunner CancellationToken.None command

    /// Read the exit code as a yes/no answer, cancellable through `cancellationToken`.
    [<Extension>]
    static member Probe(command: Command, cancellationToken: CancellationToken) =
        Runner.probe CommandVerbs.DefaultRunner cancellationToken command

    /// Require a zero/accepted exit and parse the trimmed stdout into a `'T`; a thrown parser error
    /// becomes `ProcessError.Parse`.
    [<Extension>]
    static member Parse(command: Command, parser: System.Func<string, 'T>) =
        Runner.parse CommandVerbs.DefaultRunner CancellationToken.None parser.Invoke command

    /// `Parse`, cancellable through `cancellationToken`.
    [<Extension>]
    static member Parse(command: Command, parser: System.Func<string, 'T>, cancellationToken: CancellationToken) =
        Runner.parse CommandVerbs.DefaultRunner cancellationToken parser.Invoke command

    /// Like `Parse`, but the parser returns its own `Result` (its error becomes `Parse`).
    [<Extension>]
    static member TryParse(command: Command, parser: System.Func<string, Result<'T, string>>) =
        Runner.tryParse CommandVerbs.DefaultRunner CancellationToken.None parser.Invoke command

    /// `TryParse`, cancellable through `cancellationToken`.
    [<Extension>]
    static member TryParse
        (command: Command, parser: System.Func<string, Result<'T, string>>, cancellationToken: CancellationToken)
        =
        Runner.tryParse CommandVerbs.DefaultRunner cancellationToken parser.Invoke command

    /// The first stdout line satisfying `predicate`, or `None` if stdout closes without a match.
    [<Extension>]
    static member FirstLine(command: Command, predicate: System.Func<string, bool>) =
        Runner.firstLine CommandVerbs.DefaultRunner CancellationToken.None predicate.Invoke command

    /// `FirstLine`, cancellable through `cancellationToken`.
    [<Extension>]
    static member FirstLine
        (command: Command, predicate: System.Func<string, bool>, cancellationToken: CancellationToken)
        =
        Runner.firstLine CommandVerbs.DefaultRunner cancellationToken predicate.Invoke command
