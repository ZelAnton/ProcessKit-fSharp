namespace ProcessKit

open System
open System.Threading

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

    /// Build the command for `args` and run it (requiring a zero/accepted exit), returning trimmed
    /// stdout. Cancellable through `cancellationToken`.
    member this.RunAsync(args: seq<string>, cancellationToken: CancellationToken) =
        Runner.run config.Runner cancellationToken (this.Command args)

    /// `Run` against `CancellationToken.None`.
    member this.RunAsync(args: seq<string>) =
        this.RunAsync(args, CancellationToken.None)

    /// Build the command for `args` and run it (requiring a zero/accepted exit), discarding output.
    member this.RunUnitAsync(args: seq<string>, cancellationToken: CancellationToken) =
        Runner.runUnit config.Runner cancellationToken (this.Command args)

    /// `RunUnit` against `CancellationToken.None`.
    member this.RunUnitAsync(args: seq<string>) =
        this.RunUnitAsync(args, CancellationToken.None)

    /// Build the command for `args` and run it to completion (a non-zero exit is data).
    member this.OutputStringAsync(args: seq<string>, cancellationToken: CancellationToken) =
        Runner.outputString config.Runner cancellationToken (this.Command args)

    /// `OutputString` against `CancellationToken.None`.
    member this.OutputStringAsync(args: seq<string>) =
        this.OutputStringAsync(args, CancellationToken.None)

    /// Build the command for `args` and run it to completion, capturing stdout as raw bytes.
    member this.OutputBytesAsync(args: seq<string>, cancellationToken: CancellationToken) =
        Runner.outputBytes config.Runner cancellationToken (this.Command args)

    /// `OutputBytes` against `CancellationToken.None`.
    member this.OutputBytesAsync(args: seq<string>) =
        this.OutputBytesAsync(args, CancellationToken.None)

    /// The exit code; a signal kill or timeout errors instead of inventing a sentinel.
    member this.ExitCodeAsync(args: seq<string>, cancellationToken: CancellationToken) =
        Runner.exitCode config.Runner cancellationToken (this.Command args)

    /// `ExitCode` against `CancellationToken.None`.
    member this.ExitCodeAsync(args: seq<string>) =
        this.ExitCodeAsync(args, CancellationToken.None)

    /// Read the exit code as a yes/no answer: 0 -> true, 1 -> false, anything else errors.
    member this.ProbeAsync(args: seq<string>, cancellationToken: CancellationToken) =
        Runner.probe config.Runner cancellationToken (this.Command args)

    /// `Probe` against `CancellationToken.None`.
    member this.ProbeAsync(args: seq<string>) =
        this.ProbeAsync(args, CancellationToken.None)

    /// Start the command for `args` and return a live `RunningProcess`.
    member this.StartAsync(args: seq<string>, cancellationToken: CancellationToken) =
        Runner.start config.Runner cancellationToken (this.Command args)

    /// `Start` against `CancellationToken.None`.
    member this.StartAsync(args: seq<string>) =
        this.StartAsync(args, CancellationToken.None)

    /// Require a zero/accepted exit and parse the trimmed stdout into a `'T`; a thrown parser error
    /// becomes `ProcessError.Parse`.
    member this.ParseAsync(args: seq<string>, parser: Func<string, 'T>, cancellationToken: CancellationToken) =
        ArgumentNullException.ThrowIfNull parser
        Runner.parse config.Runner cancellationToken parser.Invoke (this.Command args)

    /// `Parse` against `CancellationToken.None`.
    member this.ParseAsync(args: seq<string>, parser: Func<string, 'T>) =
        this.ParseAsync(args, parser, CancellationToken.None)

    /// Like `Parse`, but the parser returns its own `Result` (its error message becomes `Parse`).
    member this.TryParseAsync
        (args: seq<string>, parser: Func<string, Result<'T, string>>, cancellationToken: CancellationToken)
        =
        ArgumentNullException.ThrowIfNull parser
        Runner.tryParse config.Runner cancellationToken parser.Invoke (this.Command args)

    /// `TryParse` against `CancellationToken.None`.
    member this.TryParseAsync(args: seq<string>, parser: Func<string, Result<'T, string>>) =
        this.TryParseAsync(args, parser, CancellationToken.None)

    /// The first stdout line satisfying `predicate`, or `None` if stdout closes without a match.
    member this.FirstLineAsync(args: seq<string>, predicate: Func<string, bool>, cancellationToken: CancellationToken) =
        ArgumentNullException.ThrowIfNull predicate
        Runner.firstLine config.Runner cancellationToken predicate.Invoke (this.Command args)

    /// `FirstLine` against `CancellationToken.None`.
    member this.FirstLineAsync(args: seq<string>, predicate: Func<string, bool>) =
        this.FirstLineAsync(args, predicate, CancellationToken.None)

/// Pipe-friendly entry point for `CliClient`.
[<RequireQualifiedAccess>]
module CliClient =

    /// A client for `program`, run through the default `JobRunner`.
    let create (program: string) = CliClient(program)
