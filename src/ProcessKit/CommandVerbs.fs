namespace ProcessKit

open System
open System.Runtime.CompilerServices
open System.Threading

/// Default-runner convenience verbs on `Command`, callable from F# and C# as
/// `command.StartAsync()` / `command.RunAsync()` etc. They use a shared `JobRunner`; for a custom or
/// injected runner, go through `Runner.*` or call the runner directly.
[<Extension>]
type CommandVerbs =

    static member val internal DefaultRunner: IProcessRunner = JobRunner()

    /// Start the command and return a live `RunningProcess`.
    [<Extension>]
    static member StartAsync(command: Command) =
        Runner.start CommandVerbs.DefaultRunner CancellationToken.None command

    /// Start the command, cancellable through `cancellationToken`.
    [<Extension>]
    static member StartAsync(command: Command, cancellationToken: CancellationToken) =
        Runner.start CommandVerbs.DefaultRunner cancellationToken command

    /// Require a zero exit and return stdout, trailing whitespace trimmed.
    [<Extension>]
    static member RunAsync(command: Command) =
        Runner.run CommandVerbs.DefaultRunner CancellationToken.None command

    /// Require a zero exit and return stdout, cancellable through `cancellationToken`.
    [<Extension>]
    static member RunAsync(command: Command, cancellationToken: CancellationToken) =
        Runner.run CommandVerbs.DefaultRunner cancellationToken command

    /// Require a zero exit, discarding the captured output.
    [<Extension>]
    static member RunUnitAsync(command: Command) =
        Runner.runUnit CommandVerbs.DefaultRunner CancellationToken.None command

    /// Require a zero exit (discarding output), cancellable through `cancellationToken`.
    [<Extension>]
    static member RunUnitAsync(command: Command, cancellationToken: CancellationToken) =
        Runner.runUnit CommandVerbs.DefaultRunner cancellationToken command

    /// Run to completion, capturing stdout as decoded text (a non-zero exit is data).
    [<Extension>]
    static member OutputStringAsync(command: Command) =
        Runner.outputString CommandVerbs.DefaultRunner CancellationToken.None command

    /// Run to completion capturing stdout as text, cancellable through `cancellationToken`.
    [<Extension>]
    static member OutputStringAsync(command: Command, cancellationToken: CancellationToken) =
        Runner.outputString CommandVerbs.DefaultRunner cancellationToken command

    /// Run to completion, capturing stdout as raw bytes.
    [<Extension>]
    static member OutputBytesAsync(command: Command) =
        Runner.outputBytes CommandVerbs.DefaultRunner CancellationToken.None command

    /// Run to completion capturing stdout as raw bytes, cancellable through `cancellationToken`.
    [<Extension>]
    static member OutputBytesAsync(command: Command, cancellationToken: CancellationToken) =
        Runner.outputBytes CommandVerbs.DefaultRunner cancellationToken command

    /// The exit code; a signal kill or timeout errors instead of inventing a sentinel code.
    [<Extension>]
    static member ExitCodeAsync(command: Command) =
        Runner.exitCode CommandVerbs.DefaultRunner CancellationToken.None command

    /// The exit code, cancellable through `cancellationToken`.
    [<Extension>]
    static member ExitCodeAsync(command: Command, cancellationToken: CancellationToken) =
        Runner.exitCode CommandVerbs.DefaultRunner cancellationToken command

    /// Read the exit code as a yes/no answer: 0 -> true, 1 -> false, anything else errors.
    [<Extension>]
    static member ProbeAsync(command: Command) =
        Runner.probe CommandVerbs.DefaultRunner CancellationToken.None command

    /// Read the exit code as a yes/no answer, cancellable through `cancellationToken`.
    [<Extension>]
    static member ProbeAsync(command: Command, cancellationToken: CancellationToken) =
        Runner.probe CommandVerbs.DefaultRunner cancellationToken command

    /// Require a zero/accepted exit and parse the trimmed stdout into a `'T`; a thrown parser error
    /// becomes `ProcessError.Parse`.
    [<Extension>]
    static member ParseAsync(command: Command, parser: Func<string, 'T>) =
        ArgumentNullException.ThrowIfNull parser
        Runner.parse CommandVerbs.DefaultRunner CancellationToken.None parser.Invoke command

    /// `ParseAsync`, cancellable through `cancellationToken`.
    [<Extension>]
    static member ParseAsync(command: Command, parser: Func<string, 'T>, cancellationToken: CancellationToken) =
        ArgumentNullException.ThrowIfNull parser
        Runner.parse CommandVerbs.DefaultRunner cancellationToken parser.Invoke command

    /// Like `ParseAsync`, but with the standard .NET try-parse shape: pass a BCL parser like
    /// `int.TryParse` with an explicit type argument (`TryParseAsync&lt;int&gt;(int.TryParse)` — needed
    /// because BCL `TryParse` is overloaded). A `false` return becomes `ProcessError.Parse`.
    /// (F# can use the `Result`-returning `Runner.tryParse`.)
    [<Extension>]
    static member TryParseAsync(command: Command, parser: TryParser<'T>) =
        ArgumentNullException.ThrowIfNull parser
        Runner.tryParse CommandVerbs.DefaultRunner CancellationToken.None (TryParser.toResult parser) command

    /// `TryParseAsync`, cancellable through `cancellationToken`.
    [<Extension>]
    static member TryParseAsync(command: Command, parser: TryParser<'T>, cancellationToken: CancellationToken) =
        ArgumentNullException.ThrowIfNull parser
        Runner.tryParse CommandVerbs.DefaultRunner cancellationToken (TryParser.toResult parser) command

    /// The first stdout line satisfying `predicate`, or `None` if stdout closes without a match.
    [<Extension>]
    static member FirstLineAsync(command: Command, predicate: Func<string, bool>) =
        ArgumentNullException.ThrowIfNull predicate
        Runner.firstLine CommandVerbs.DefaultRunner CancellationToken.None predicate.Invoke command

    /// `FirstLineAsync`, cancellable through `cancellationToken`.
    [<Extension>]
    static member FirstLineAsync
        (command: Command, predicate: Func<string, bool>, cancellationToken: CancellationToken)
        =
        ArgumentNullException.ThrowIfNull predicate
        Runner.firstLine CommandVerbs.DefaultRunner cancellationToken predicate.Invoke command
