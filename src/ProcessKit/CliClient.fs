namespace ProcessKit

open System
open System.Diagnostics.CodeAnalysis
open System.Runtime.InteropServices
open System.Text.Json
open System.Text.Json.Serialization.Metadata
open System.Threading
open System.Threading.Tasks

/// The immutable configuration behind a `CliClient`: the runner, and a *template* `Command` carrying
/// the program plus any shared defaults. Internal — built through the builder.
type internal CliClientConfig =
    { Runner: IProcessRunner
      Template: Command }

module internal CliClientConfig =

    let create (program: string) =
        { Runner = JobRunner()
          Template = Command.create program }

/// A reusable handle to one command-line program with shared defaults applied to every invocation.
/// The defaults live in a *template* `Command`, configured with the full `Command` builder via
/// `WithDefaults` (timeout, working directory, environment, encoding, ok-codes, retry, logger, …) —
/// no separate, partial setter API to learn. Build `Command`s for argument lists
/// (`client.Command [ "status" ]`) or run them straight through the client's runner
/// (`client.RunAsync [ "status" ]`).
[<Sealed>]
type CliClient internal (config: CliClientConfig) =

    /// A client for `program`, run through the default `JobRunner`.
    new(program: string) =
        ArgumentNullException.ThrowIfNull program
        CliClient(CliClientConfig.create program)

    /// Run every command this client builds through `runner` instead of the default `JobRunner`
    /// (e.g. a shared `ProcessGroup`).
    member _.WithRunner(runner: IProcessRunner) =
        ArgumentNullException.ThrowIfNull runner
        CliClient({ config with Runner = runner })

    /// Configure the shared defaults by transforming the template `Command` with the full builder,
    /// e.g. `client.WithDefaults(fun c -> c.CurrentDir(repo).Timeout(ts).Env("K", "V"))`. Composable.
    /// Use the builder to set options; don't return a fresh `Command.create` (it would re-point the
    /// client at a different program and drop the accumulated defaults).
    member _.WithDefaults(configure: Func<Command, Command>) =
        ArgumentNullException.ThrowIfNull configure

        CliClient(
            { config with
                Template = configure.Invoke config.Template }
        )

    /// The runner every command is run through.
    member _.Runner = config.Runner

    /// Build a configured `Command` for `args` (the template's shared defaults applied).
    member _.Command(args: seq<string>) : Command =
        ArgumentNullException.ThrowIfNull args
        config.Template.Args args

    // All verbs route through `config.Runner` (not the default `JobRunner`), so a `CliClient` built
    // on a test/shared runner — `WithRunner(scriptedRunner)` / `WithRunner(group)` — stays hermetic:
    // every verb, not just the captured output, goes through the configured seam.

    // The `cancellationToken` is optional on every verb and defaults to `CancellationToken.None`, so
    // `client.RunAsync [ "status" ]` and `client.RunAsync([ "status" ], ct)` are the same method.

    /// Build the command for `args` and run it (requiring a zero/accepted exit), returning trimmed stdout.
    member this.RunAsync(args: seq<string>, [<Optional>] cancellationToken: CancellationToken) =
        Runner.run config.Runner cancellationToken (this.Command args)

    /// Build the command for `args` and run it (requiring a zero/accepted exit), discarding output.
    member this.RunUnitAsync(args: seq<string>, [<Optional>] cancellationToken: CancellationToken) =
        Runner.runUnit config.Runner cancellationToken (this.Command args)

    /// Build the command for `args` and run it to completion (a non-zero exit is data).
    member this.OutputStringAsync(args: seq<string>, [<Optional>] cancellationToken: CancellationToken) =
        Runner.outputString config.Runner cancellationToken (this.Command args)

    /// Build the command for `args` and run it to completion, capturing stdout as raw bytes.
    member this.OutputBytesAsync(args: seq<string>, [<Optional>] cancellationToken: CancellationToken) =
        Runner.outputBytes config.Runner cancellationToken (this.Command args)

    /// The exit code; a signal kill or timeout errors instead of inventing a sentinel.
    member this.ExitCodeAsync(args: seq<string>, [<Optional>] cancellationToken: CancellationToken) =
        Runner.exitCode config.Runner cancellationToken (this.Command args)

    /// Read the exit code as a yes/no answer: 0 -> true, 1 -> false, anything else errors.
    member this.ProbeAsync(args: seq<string>, [<Optional>] cancellationToken: CancellationToken) =
        Runner.probe config.Runner cancellationToken (this.Command args)

    /// Start the command for `args` and return a live `RunningProcess`.
    member this.StartAsync(args: seq<string>, [<Optional>] cancellationToken: CancellationToken) =
        Runner.start config.Runner cancellationToken (this.Command args)

    /// Require a zero/accepted exit and parse the trimmed stdout into a `'T`; a thrown parser error
    /// becomes `ProcessError.Parse`.
    member this.ParseAsync
        (args: seq<string>, parser: Func<string, 'T>, [<Optional>] cancellationToken: CancellationToken)
        =
        ArgumentNullException.ThrowIfNull parser
        Runner.parse config.Runner cancellationToken parser.Invoke (this.Command args)

    /// Like `ParseAsync`, but with the standard .NET try-parse shape: pass a BCL parser like
    /// `int.TryParse` with an explicit type argument (`TryParseAsync&lt;int&gt;(int.TryParse)` — needed
    /// because BCL `TryParse` is overloaded). A `false` return becomes `ProcessError.Parse`.
    /// (F# can use the `Result`-returning `Runner.tryParse`.)
    member this.TryParseAsync
        (args: seq<string>, parser: TryParser<'T>, [<Optional>] cancellationToken: CancellationToken)
        =
        ArgumentNullException.ThrowIfNull parser
        Runner.tryParse config.Runner cancellationToken (TryParser.toResult parser) (this.Command args)

    /// Build the command for `args`, require a zero/accepted exit, and deserialize the trimmed stdout
    /// as JSON into a `'T` via `System.Text.Json` (`options` omitted uses the BCL defaults); invalid
    /// JSON becomes `ProcessError.Parse`, just like `ParseAsync`. Give an explicit type argument — there
    /// is no parser argument to infer `'T` from.
    ///
    /// **Trimming / AOT:** deserializes via reflection-based `System.Text.Json`
    /// (`JsonSerializer.Deserialize(string, Type, JsonSerializerOptions)`), so it is not trim-/AOT-safe —
    /// pass `options` with a source-generated `JsonSerializerContext`/`JsonTypeInfo&lt;'T&gt;` resolver, or
    /// avoid this verb, in a trimmed/NativeAOT app.
    [<RequiresUnreferencedCode "Deserializes stdout by reflection via System.Text.Json; give options a source-generated JsonSerializerContext, or avoid this verb, in a trimmed app.">]
    [<RequiresDynamicCode "Deserializes stdout by reflection via System.Text.Json; give options a source-generated JsonSerializerContext, or avoid this verb, in a NativeAOT app.">]
    member this.OutputJsonAsync<'T>
        (
            args: seq<string>,
            [<Optional>] options: JsonSerializerOptions | null,
            [<Optional>] cancellationToken: CancellationToken
        ) =
        Runner.outputJson<'T> config.Runner cancellationToken (Option.ofObj options) (this.Command args)

    /// Build the command for `args`, require a zero/accepted exit, and deserialize the trimmed stdout
    /// using source-generated `JsonTypeInfo<'T>` metadata. Invalid JSON becomes `ProcessError.Parse`;
    /// unlike the `JsonSerializerOptions` overload, this overload is safe for trimmed and NativeAOT
    /// applications.
    member this.OutputJsonAsync<'T>
        (args: seq<string>, typeInfo: JsonTypeInfo<'T>, [<Optional>] cancellationToken: CancellationToken)
        =
        ArgumentNullException.ThrowIfNull typeInfo
        Runner.outputJsonTyped<'T> config.Runner cancellationToken typeInfo (this.Command args)

    /// The first stdout line satisfying `predicate`, or `None` if stdout closes without a match.
    member this.FirstLineAsync
        (args: seq<string>, predicate: Func<string, bool>, [<Optional>] cancellationToken: CancellationToken)
        =
        ArgumentNullException.ThrowIfNull predicate
        Runner.firstLine config.Runner cancellationToken predicate.Invoke (this.Command args)

    /// Preflight-check that this client's program can be resolved to a real executable on this host,
    /// WITHOUT spawning it — the `doctor`/install-wizard use case ("is this tool installed?"), cheaper
    /// and side-effect-free next to probing availability by actually running the program
    /// (`ProbeAsync`). A thin wrapper over `Exec.which`/`Native.Common.resolveProgram` (the same
    /// PATH/PATHEXT-aware logic the spawn path itself falls back on for its own `NotFound`
    /// diagnostic), so this check and an actual run of the client's program never disagree on
    /// found-vs-not-found.
    ///
    /// **Resolution is always LOCAL — never delegated to `Runner`.** Availability is a fact about the
    /// host's `PATH`/filesystem, independent of which `IProcessRunner` a command is eventually run
    /// through, so a test double/mock injected via `WithRunner` (e.g. a `ScriptedRunner`) has no
    /// bearing on this result: it always probes the real host, even when `Runner` is a double. A
    /// caller unit-testing wrapper-app logic around this check should stub around
    /// `EnsureAvailableAsync` itself (branch on an injected flag, or skip calling it), rather than
    /// expect a mocked `Runner` to control it.
    member _.EnsureAvailableAsync() : Task<Result<string, ProcessError>> =
        Task.FromResult(Native.Common.resolveProgram config.Template.Program)

    /// Resolve this client's program to a full path WITHOUT spawning it — the same no-spawn, synchronous,
    /// side-effect-free preflight as `Command.ResolveProgram`, for the template command behind the client
    /// (its shared `WithDefaults` environment and `PreferLocal` applied). Resolution is against the
    /// **effective child** `PATH` — the client's own `Env`/`EnvRemove`/`EnvClear` (a `PATH` override) plus
    /// its prefer-local directories, searched exactly as a real run of the client would — and on a miss it
    /// returns the SAME typed `ProcessError.NotFound`/`Searched` a real run of the client would fail with
    /// (one shared resolver, never a separate copy).
    ///
    /// **Differs from `EnsureAvailableAsync`.** `EnsureAvailableAsync` is a thin `Exec.which` over the
    /// CURRENT PROCESS's `PATH` (no prefer-local) — "is this tool installed on the host". `ResolveProgram`
    /// reflects the client's OWN configured environment/prefer-local — "will this client find its program".
    /// Like `EnsureAvailableAsync`, resolution is always **local**, never delegated to `Runner`:
    /// availability is a fact about the host's `PATH`/filesystem, independent of which `IProcessRunner` a
    /// command eventually runs through, so a test double injected via `WithRunner` has no bearing on it.
    member _.ResolveProgram() : Result<string, ProcessError> =
        Native.Common.resolveCommandProgram config.Template

/// Pipe-friendly entry point for `CliClient`.
[<RequireQualifiedAccess>]
module CliClient =

    /// A client for `program`, run through the default `JobRunner`.
    let create (program: string) = CliClient(program)
