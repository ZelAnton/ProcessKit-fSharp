namespace ProcessKit

open System
open System.Diagnostics.CodeAnalysis
open System.Runtime.CompilerServices
open System.Runtime.InteropServices
open System.Text.Json
open System.Threading

/// Default-runner convenience verbs on `Command`, callable from F# and C# as
/// `command.StartAsync()` / `command.RunAsync()` etc. They use a shared `JobRunner`; for a custom or
/// injected runner, go through `Runner.*` or call the runner directly. The `cancellationToken` is
/// optional and defaults to `CancellationToken.None`.
[<Extension>]
type CommandVerbs =

    static member val internal DefaultRunner: IProcessRunner = JobRunner()

    /// Start the command and return a live `RunningProcess`.
    [<Extension>]
    static member StartAsync(command: Command, [<Optional>] cancellationToken: CancellationToken) =
        Runner.start CommandVerbs.DefaultRunner cancellationToken command

    /// Require a zero exit and return stdout, trailing whitespace trimmed.
    [<Extension>]
    static member RunAsync(command: Command, [<Optional>] cancellationToken: CancellationToken) =
        Runner.run CommandVerbs.DefaultRunner cancellationToken command

    /// Require a zero exit, discarding the captured output.
    [<Extension>]
    static member RunUnitAsync(command: Command, [<Optional>] cancellationToken: CancellationToken) =
        Runner.runUnit CommandVerbs.DefaultRunner cancellationToken command

    /// Run to completion, capturing stdout as decoded text (a non-zero exit is data).
    [<Extension>]
    static member OutputStringAsync(command: Command, [<Optional>] cancellationToken: CancellationToken) =
        Runner.outputString CommandVerbs.DefaultRunner cancellationToken command

    /// Run to completion, capturing stdout as raw bytes.
    [<Extension>]
    static member OutputBytesAsync(command: Command, [<Optional>] cancellationToken: CancellationToken) =
        Runner.outputBytes CommandVerbs.DefaultRunner cancellationToken command

    /// The exit code; a signal kill or timeout errors instead of inventing a sentinel code.
    [<Extension>]
    static member ExitCodeAsync(command: Command, [<Optional>] cancellationToken: CancellationToken) =
        Runner.exitCode CommandVerbs.DefaultRunner cancellationToken command

    /// Read the exit code as a yes/no answer: 0 -> true, 1 -> false, anything else errors.
    [<Extension>]
    static member ProbeAsync(command: Command, [<Optional>] cancellationToken: CancellationToken) =
        Runner.probe CommandVerbs.DefaultRunner cancellationToken command

    /// Require a zero/accepted exit and parse the trimmed stdout into a `'T`; a thrown parser error
    /// becomes `ProcessError.Parse`.
    [<Extension>]
    static member ParseAsync
        (command: Command, parser: Func<string, 'T>, [<Optional>] cancellationToken: CancellationToken)
        =
        ArgumentNullException.ThrowIfNull parser
        Runner.parse CommandVerbs.DefaultRunner cancellationToken parser.Invoke command

    /// Like `ParseAsync`, but with the standard .NET try-parse shape: pass a BCL parser like
    /// `int.TryParse` with an explicit type argument (`TryParseAsync&lt;int&gt;(int.TryParse)` — needed
    /// because BCL `TryParse` is overloaded). A `false` return becomes `ProcessError.Parse`.
    /// (F# can use the `Result`-returning `Runner.tryParse`.)
    [<Extension>]
    static member TryParseAsync
        (command: Command, parser: TryParser<'T>, [<Optional>] cancellationToken: CancellationToken)
        =
        ArgumentNullException.ThrowIfNull parser
        Runner.tryParse CommandVerbs.DefaultRunner cancellationToken (TryParser.toResult parser) command

    /// Require a zero/accepted exit and deserialize the trimmed stdout as JSON into a `'T` via
    /// `System.Text.Json` (`options` omitted uses the BCL defaults); invalid JSON becomes
    /// `ProcessError.Parse`, just like `ParseAsync`. Give an explicit type argument, e.g.
    /// `cmd.OutputJsonAsync&lt;MyRecord&gt;()` — there is no parser argument to infer `'T` from.
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
            command: Command,
            [<Optional>] options: JsonSerializerOptions | null,
            [<Optional>] cancellationToken: CancellationToken
        ) =
        Runner.outputJson<'T> CommandVerbs.DefaultRunner cancellationToken (Option.ofObj options) command

    /// The first stdout line satisfying `predicate`, or `None` if stdout closes without a match.
    [<Extension>]
    static member FirstLineAsync
        (command: Command, predicate: Func<string, bool>, [<Optional>] cancellationToken: CancellationToken)
        =
        ArgumentNullException.ThrowIfNull predicate
        Runner.firstLine CommandVerbs.DefaultRunner cancellationToken predicate.Invoke command
