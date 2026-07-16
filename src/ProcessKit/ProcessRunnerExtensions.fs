namespace ProcessKit

open System
open System.Diagnostics.CodeAnalysis
open System.Runtime.CompilerServices
open System.Runtime.InteropServices
open System.Text.Json
open System.Text.Json.Serialization.Metadata
open System.Threading

/// The full run-verb vocabulary on *any* `IProcessRunner`, layered over the three-method seam
/// (`CaptureStringAsync`/`CaptureBytesAsync`/`SpawnAsync`). So a chosen or injected runner — a shared
/// `ProcessGroup`, a `ScriptedRunner`, a `JobRunner` — gets `run`/`exitCode`/`probe`/`parse`/… uniformly
/// (`group.RunAsync command`, `scripted.ProbeAsync command`), callable from F# and C#. The verb *logic* lives
/// once in the `Runner` module; these are thin sugar over it.
///
/// Retry contract: every capture verb here applies the command's `Retry` policy (it routes through the
/// `Runner` module). The `cancellationToken` is optional and defaults to `CancellationToken.None`, so
/// `runner.RunAsync command` and `runner.RunAsync(command, ct)` are the same method and retry identically.
/// (Because the seam primitives are named `Capture*`/`Spawn`, the verb names never collide with them, so
/// adding a token can't silently bypass retry.) For a raw, single, no-retry capture call the seam primitive
/// directly: `runner.CaptureStringAsync(command, ct)`. Streaming verbs (`StartAsync`/`FirstLineAsync`) never retry.
[<Extension>]
type ProcessRunnerExtensions =

    /// Require a zero/accepted exit and return stdout, trailing whitespace trimmed.
    [<Extension>]
    static member RunAsync
        (runner: IProcessRunner, command: Command, [<Optional>] cancellationToken: CancellationToken)
        =
        Runner.run runner cancellationToken command

    /// Require a zero/accepted exit, discarding the captured output.
    [<Extension>]
    static member RunUnitAsync
        (runner: IProcessRunner, command: Command, [<Optional>] cancellationToken: CancellationToken)
        =
        Runner.runUnit runner cancellationToken command

    /// Run to completion, capturing stdout as decoded text (a non-zero exit is data). Applies the
    /// command's `Retry` policy; for a single raw capture with no retry, call the seam primitive
    /// directly: `runner.CaptureStringAsync(command, ct)`.
    [<Extension>]
    static member OutputStringAsync
        (runner: IProcessRunner, command: Command, [<Optional>] cancellationToken: CancellationToken)
        =
        Runner.outputString runner cancellationToken command

    /// Run to completion, capturing stdout as raw bytes (a non-zero exit is data). Applies the
    /// command's `Retry` policy; the raw seam `runner.CaptureBytesAsync(command, ct)` is the no-retry path.
    [<Extension>]
    static member OutputBytesAsync
        (runner: IProcessRunner, command: Command, [<Optional>] cancellationToken: CancellationToken)
        =
        Runner.outputBytes runner cancellationToken command

    /// The exit code; a signal kill or timeout errors instead of inventing a sentinel.
    [<Extension>]
    static member ExitCodeAsync
        (runner: IProcessRunner, command: Command, [<Optional>] cancellationToken: CancellationToken)
        =
        Runner.exitCode runner cancellationToken command

    /// Read the exit code as a yes/no answer: 0 -> true, 1 -> false, anything else errors.
    [<Extension>]
    static member ProbeAsync
        (runner: IProcessRunner, command: Command, [<Optional>] cancellationToken: CancellationToken)
        =
        Runner.probe runner cancellationToken command

    /// Start the command and return a live `RunningProcess`. Forwards to the runner's `SpawnAsync` seam.
    /// `cancellationToken` is checked once, before the spawn (an already-cancelled token reports
    /// `ProcessError.Cancelled` and starts nothing); after the child is running neither it nor the
    /// command's `CancelOn` token is tracked — drive and cancel the live handle yourself (dispose it or
    /// call its `Kill`). The completion verbs, by contrast, watch the token for the whole run.
    [<Extension>]
    static member StartAsync
        (runner: IProcessRunner, command: Command, [<Optional>] cancellationToken: CancellationToken)
        =
        Runner.start runner cancellationToken command

    /// Require a zero/accepted exit and parse the trimmed stdout into a `'T`; a thrown parser error
    /// becomes `ProcessError.Parse`.
    [<Extension>]
    static member ParseAsync
        (
            runner: IProcessRunner,
            command: Command,
            parser: Func<string, 'T>,
            [<Optional>] cancellationToken: CancellationToken
        ) =
        ArgumentNullException.ThrowIfNull parser
        Runner.parse runner cancellationToken parser.Invoke command

    /// Like `ParseAsync`, but with the standard .NET try-parse shape: pass a BCL parser like
    /// `int.TryParse` with an explicit type argument (`TryParseAsync&lt;int&gt;(int.TryParse)` — needed
    /// because BCL `TryParse` is overloaded). A `false` return becomes `ProcessError.Parse`.
    /// (F# can use the `Result`-returning `Runner.tryParse`.)
    [<Extension>]
    static member TryParseAsync
        (
            runner: IProcessRunner,
            command: Command,
            parser: TryParser<'T>,
            [<Optional>] cancellationToken: CancellationToken
        ) =
        ArgumentNullException.ThrowIfNull parser
        Runner.tryParse runner cancellationToken (TryParser.toResult parser) command

    /// Require a zero/accepted exit and deserialize the trimmed stdout as JSON into a `'T` via
    /// `System.Text.Json` (`options` omitted uses the BCL defaults); invalid JSON becomes
    /// `ProcessError.Parse`, just like `ParseAsync`. Give an explicit type argument — there is no parser
    /// argument to infer `'T` from.
    ///
    /// **Trimming / AOT:** deserializes via reflection-based `System.Text.Json`
    /// (`JsonSerializer.Deserialize(string, Type, JsonSerializerOptions)`), so it is not trim-/AOT-safe —
    /// pass `options` with a source-generated `JsonSerializerContext`/`JsonTypeInfo&lt;'T&gt;` resolver, or
    /// avoid this verb, in a trimmed/NativeAOT app.
    [<Extension>]
    [<RequiresUnreferencedCode "Deserializes stdout by reflection via System.Text.Json; give options a source-generated JsonSerializerContext, or avoid this verb, in a trimmed app.">]
    [<RequiresDynamicCode "Deserializes stdout by reflection via System.Text.Json; give options a source-generated JsonSerializerContext, or avoid this verb, in a NativeAOT app.">]
    static member OutputJsonAsync<'T>
        (
            runner: IProcessRunner,
            command: Command,
            [<Optional>] options: JsonSerializerOptions | null,
            [<Optional>] cancellationToken: CancellationToken
        ) =
        Runner.outputJson<'T> runner cancellationToken (Option.ofObj options) command

    /// Require a zero/accepted exit and deserialize the trimmed stdout using source-generated
    /// `JsonTypeInfo<'T>` metadata. Invalid JSON becomes `ProcessError.Parse`; unlike the
    /// `JsonSerializerOptions` overload, this overload is safe for trimmed and NativeAOT applications.
    [<Extension>]
    static member OutputJsonAsync<'T>
        (
            runner: IProcessRunner,
            command: Command,
            typeInfo: JsonTypeInfo<'T>,
            [<Optional>] cancellationToken: CancellationToken
        ) =
        ArgumentNullException.ThrowIfNull typeInfo
        Runner.outputJsonTyped<'T> runner cancellationToken typeInfo command

    /// The first stdout line satisfying `predicate`, or `None` if stdout closes without a match.
    [<Extension>]
    static member FirstLineAsync
        (
            runner: IProcessRunner,
            command: Command,
            predicate: Func<string, bool>,
            [<Optional>] cancellationToken: CancellationToken
        ) =
        ArgumentNullException.ThrowIfNull predicate
        Runner.firstLine runner cancellationToken predicate.Invoke command
