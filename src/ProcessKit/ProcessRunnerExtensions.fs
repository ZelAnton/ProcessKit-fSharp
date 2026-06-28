namespace ProcessKit

open System
open System.Runtime.CompilerServices
open System.Threading

/// The full run-verb vocabulary on *any* `IProcessRunner`, derived from the three-method seam
/// (`OutputStringAsync`/`OutputBytesAsync`/`StartAsync`). So a chosen or injected runner — a shared `ProcessGroup`,
/// a `ScriptedRunner`, a `JobRunner` — gets `run`/`exitCode`/`probe`/`parse`/… uniformly
/// (`group.RunAsync command`, `scripted.ProbeAsync command`), callable from F# and C#. The verb *logic* lives
/// once in the `Runner` module; these are thin sugar over it.
///
/// Retry contract: the verbs here apply the command's `Retry` policy (they route through the `Runner`
/// module), exactly like the `Command`/`CliClient` verbs. One asymmetry to know: `OutputStringAsync`/
/// `OutputBytesAsync` have no `CancellationToken` overload (it would be shadowed by the seam), so a call
/// like `runner.OutputStringAsync(command, ct)` binds to the raw `IProcessRunner` seam method and does NOT
/// retry — for capture with a token *and* retry, call `Runner.outputString runner ct command`. The
/// raw `IProcessRunner.OutputStringAsync`/`OutputBytesAsync`/`StartAsync` seam never retries; streaming verbs
/// (`StartAsync`/`FirstLineAsync`) never retry on any surface.
[<Extension>]
type ProcessRunnerExtensions =

    /// Require a zero/accepted exit and return stdout, trailing whitespace trimmed.
    [<Extension>]
    static member RunAsync(runner: IProcessRunner, command: Command) =
        Runner.run runner CancellationToken.None command

    /// `RunAsync`, cancellable through `cancellationToken`.
    [<Extension>]
    static member RunAsync(runner: IProcessRunner, command: Command, cancellationToken: CancellationToken) =
        Runner.run runner cancellationToken command

    /// Require a zero/accepted exit, discarding the captured output.
    [<Extension>]
    static member RunUnitAsync(runner: IProcessRunner, command: Command) =
        Runner.runUnit runner CancellationToken.None command

    /// `RunUnitAsync`, cancellable through `cancellationToken`.
    [<Extension>]
    static member RunUnitAsync(runner: IProcessRunner, command: Command, cancellationToken: CancellationToken) =
        Runner.runUnit runner cancellationToken command

    /// Run to completion, capturing stdout as decoded text (a non-zero exit is data). Applies the
    /// command's `Retry` policy (like the `Command`/`CliClient` verbs); for a single uncancellable run
    /// with no retry, call the seam directly: `runner.OutputStringAsync(command, ct)`.
    [<Extension>]
    static member OutputStringAsync(runner: IProcessRunner, command: Command) =
        Runner.outputString runner CancellationToken.None command

    /// Run to completion, capturing stdout as raw bytes (a non-zero exit is data). Applies the
    /// command's `Retry` policy; the raw seam `runner.OutputBytesAsync(command, ct)` is the no-retry path.
    [<Extension>]
    static member OutputBytesAsync(runner: IProcessRunner, command: Command) =
        Runner.outputBytes runner CancellationToken.None command

    /// The exit code; a signal kill or timeout errors instead of inventing a sentinel.
    [<Extension>]
    static member ExitCodeAsync(runner: IProcessRunner, command: Command) =
        Runner.exitCode runner CancellationToken.None command

    /// `ExitCodeAsync`, cancellable through `cancellationToken`.
    [<Extension>]
    static member ExitCodeAsync(runner: IProcessRunner, command: Command, cancellationToken: CancellationToken) =
        Runner.exitCode runner cancellationToken command

    /// Read the exit code as a yes/no answer: 0 -> true, 1 -> false, anything else errors.
    [<Extension>]
    static member ProbeAsync(runner: IProcessRunner, command: Command) =
        Runner.probe runner CancellationToken.None command

    /// `ProbeAsync`, cancellable through `cancellationToken`.
    [<Extension>]
    static member ProbeAsync(runner: IProcessRunner, command: Command, cancellationToken: CancellationToken) =
        Runner.probe runner cancellationToken command

    /// Start the command and return a live `RunningProcess`. Forwards to the runner's own seam method.
    [<Extension>]
    static member StartAsync(runner: IProcessRunner, command: Command) =
        runner.StartAsync(command, CancellationToken.None)

    /// Require a zero/accepted exit and parse the trimmed stdout into a `'T`; a thrown parser error
    /// becomes `ProcessError.Parse`.
    [<Extension>]
    static member ParseAsync(runner: IProcessRunner, command: Command, parser: Func<string, 'T>) =
        ArgumentNullException.ThrowIfNull parser
        Runner.parse runner CancellationToken.None parser.Invoke command

    /// `ParseAsync`, cancellable through `cancellationToken`.
    [<Extension>]
    static member ParseAsync
        (runner: IProcessRunner, command: Command, parser: Func<string, 'T>, cancellationToken: CancellationToken)
        =
        ArgumentNullException.ThrowIfNull parser
        Runner.parse runner cancellationToken parser.Invoke command

    /// Like `ParseAsync`, but with the standard .NET try-parse shape: pass a BCL parser like
    /// `int.TryParse` with an explicit type argument (`TryParseAsync&lt;int&gt;(int.TryParse)` — needed
    /// because BCL `TryParse` is overloaded). A `false` return becomes `ProcessError.Parse`.
    /// (F# can use the `Result`-returning `Runner.tryParse`.)
    [<Extension>]
    static member TryParseAsync(runner: IProcessRunner, command: Command, parser: TryParser<'T>) =
        ArgumentNullException.ThrowIfNull parser
        Runner.tryParse runner CancellationToken.None (TryParser.toResult parser) command

    /// `TryParseAsync`, cancellable through `cancellationToken`.
    [<Extension>]
    static member TryParseAsync
        (runner: IProcessRunner, command: Command, parser: TryParser<'T>, cancellationToken: CancellationToken)
        =
        ArgumentNullException.ThrowIfNull parser
        Runner.tryParse runner cancellationToken (TryParser.toResult parser) command

    /// The first stdout line satisfying `predicate`, or `None` if stdout closes without a match.
    [<Extension>]
    static member FirstLineAsync(runner: IProcessRunner, command: Command, predicate: Func<string, bool>) =
        ArgumentNullException.ThrowIfNull predicate
        Runner.firstLine runner CancellationToken.None predicate.Invoke command

    /// `FirstLineAsync`, cancellable through `cancellationToken`.
    [<Extension>]
    static member FirstLineAsync
        (runner: IProcessRunner, command: Command, predicate: Func<string, bool>, cancellationToken: CancellationToken)
        =
        ArgumentNullException.ThrowIfNull predicate
        Runner.firstLine runner cancellationToken predicate.Invoke command
