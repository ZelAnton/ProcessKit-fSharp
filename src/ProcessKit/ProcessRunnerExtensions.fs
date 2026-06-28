namespace ProcessKit

open System
open System.Runtime.CompilerServices
open System.Threading

/// The full run-verb vocabulary on *any* `IProcessRunner`, layered over the three-method seam
/// (`CaptureStringAsync`/`CaptureBytesAsync`/`SpawnAsync`). So a chosen or injected runner — a shared
/// `ProcessGroup`, a `ScriptedRunner`, a `JobRunner` — gets `run`/`exitCode`/`probe`/`parse`/… uniformly
/// (`group.RunAsync command`, `scripted.ProbeAsync command`), callable from F# and C#. The verb *logic* lives
/// once in the `Runner` module; these are thin sugar over it.
///
/// Retry contract: every capture verb here applies the command's `Retry` policy (it routes through the
/// `Runner` module), with both a no-token and a `CancellationToken` overload that behave identically on
/// retry — exactly like the `Command`/`CliClient` verbs. (Because the seam primitives are named
/// `Capture*`/`Spawn`, the verb names never collide with them, so adding a token can't silently bypass
/// retry.) For a raw, single, no-retry capture call the seam primitive directly:
/// `runner.CaptureStringAsync(command, ct)`. Streaming verbs (`StartAsync`/`FirstLineAsync`) never retry.
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
    /// command's `Retry` policy; for a single raw capture with no retry, call the seam primitive
    /// directly: `runner.CaptureStringAsync(command, ct)`.
    [<Extension>]
    static member OutputStringAsync(runner: IProcessRunner, command: Command) =
        Runner.outputString runner CancellationToken.None command

    /// `OutputStringAsync`, cancellable through `cancellationToken` (still applies the `Retry` policy).
    [<Extension>]
    static member OutputStringAsync(runner: IProcessRunner, command: Command, cancellationToken: CancellationToken) =
        Runner.outputString runner cancellationToken command

    /// Run to completion, capturing stdout as raw bytes (a non-zero exit is data). Applies the
    /// command's `Retry` policy; the raw seam `runner.CaptureBytesAsync(command, ct)` is the no-retry path.
    [<Extension>]
    static member OutputBytesAsync(runner: IProcessRunner, command: Command) =
        Runner.outputBytes runner CancellationToken.None command

    /// `OutputBytesAsync`, cancellable through `cancellationToken` (still applies the `Retry` policy).
    [<Extension>]
    static member OutputBytesAsync(runner: IProcessRunner, command: Command, cancellationToken: CancellationToken) =
        Runner.outputBytes runner cancellationToken command

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

    /// Start the command and return a live `RunningProcess`. Forwards to the runner's `SpawnAsync` seam.
    [<Extension>]
    static member StartAsync(runner: IProcessRunner, command: Command) =
        Runner.start runner CancellationToken.None command

    /// `StartAsync`, cancellable through `cancellationToken`.
    [<Extension>]
    static member StartAsync(runner: IProcessRunner, command: Command, cancellationToken: CancellationToken) =
        Runner.start runner cancellationToken command

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
