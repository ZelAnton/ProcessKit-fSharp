namespace ProcessKit

open System
open System.Threading

/// The immutable configuration behind a `CliClient`. Internal — built through the builder.
type internal CliClientConfig =
    { Program: string
      Runner: IProcessRunner
      DefaultTimeout: TimeSpan option
      DefaultCwd: string option
      DefaultEnv: (string * string option) list
      DefaultCancelOn: CancellationToken option }

module internal CliClientConfig =

    let create (program: string) =
        { Program = program
          Runner = JobRunner()
          DefaultTimeout = None
          DefaultCwd = None
          DefaultEnv = []
          DefaultCancelOn = None }

/// A reusable handle to one command-line program with shared defaults — timeout, environment,
/// working directory, cancellation — applied to every invocation. Configure it once, then build
/// `Command`s for argument lists (`client.Command [ "status" ]`) or run them straight through the
/// client's runner (`client.Run [ "status" ]`).
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

    /// Apply `timeout` to every command this client builds.
    member _.DefaultTimeout(timeout: TimeSpan) =
        CliClient(
            { config with
                DefaultTimeout = Some timeout }
        )

    /// Run every command from this working directory unless overridden.
    member _.DefaultCurrentDir(directory: string) =
        ArgumentNullException.ThrowIfNull directory

        CliClient(
            { config with
                DefaultCwd = Some directory }
        )

    /// Set an environment variable on every command this client builds.
    member _.DefaultEnv(key: string, value: string) =
        ArgumentNullException.ThrowIfNull key
        ArgumentNullException.ThrowIfNull value

        CliClient(
            { config with
                DefaultEnv = config.DefaultEnv @ [ key, Some value ] }
        )

    /// Remove an inherited environment variable on every command this client builds.
    member _.DefaultEnvRemove(key: string) =
        ArgumentNullException.ThrowIfNull key

        CliClient(
            { config with
                DefaultEnv = config.DefaultEnv @ [ key, None ] }
        )

    /// Tie every command this client builds to `cancellationToken`.
    member _.DefaultCancelOn(cancellationToken: CancellationToken) =
        CliClient(
            { config with
                DefaultCancelOn = Some cancellationToken }
        )

    /// The runner every command is run through.
    member _.Runner = config.Runner

    /// The default timeout applied to built commands, if any.
    member _.Timeout = config.DefaultTimeout

    /// Build a configured `Command` for `args` (the shared defaults applied).
    member _.Command(args: seq<string>) : Command =
        ArgumentNullException.ThrowIfNull args
        let mutable command = Command.create config.Program |> Command.args args

        config.DefaultCwd |> Option.iter (fun dir -> command <- command.CurrentDir dir)
        config.DefaultTimeout |> Option.iter (fun t -> command <- command.Timeout t)

        config.DefaultCancelOn
        |> Option.iter (fun token -> command <- command.CancelOn token)

        for key, value in config.DefaultEnv do
            command <-
                match value with
                | Some v -> command.Env(key, v)
                | None -> command.EnvRemove key

        command

    /// Build a configured `Command` for `args`, overriding the working directory with `directory`.
    member this.CommandIn(directory: string, args: seq<string>) : Command =
        ArgumentNullException.ThrowIfNull directory
        (this.Command args).CurrentDir directory

    // All verbs route through `config.Runner` (not the default `JobRunner`), so a `CliClient` built
    // on a test/shared runner — `WithRunner(scriptedRunner)` / `WithRunner(group)` — stays hermetic:
    // every verb, not just the captured output, goes through the configured seam.

    /// Build the command for `args` and run it (requiring a zero/accepted exit), returning trimmed
    /// stdout. Cancellable through `cancellationToken`.
    member this.Run(args: seq<string>, cancellationToken: CancellationToken) =
        Runner.run config.Runner cancellationToken (this.Command args)

    /// `Run` against `CancellationToken.None`.
    member this.Run(args: seq<string>) = this.Run(args, CancellationToken.None)

    /// Build the command for `args` and run it (requiring a zero/accepted exit), discarding output.
    member this.RunUnit(args: seq<string>, cancellationToken: CancellationToken) =
        Runner.runUnit config.Runner cancellationToken (this.Command args)

    /// `RunUnit` against `CancellationToken.None`.
    member this.RunUnit(args: seq<string>) =
        this.RunUnit(args, CancellationToken.None)

    /// Build the command for `args` and run it to completion (a non-zero exit is data).
    member this.OutputString(args: seq<string>, cancellationToken: CancellationToken) =
        Runner.outputString config.Runner cancellationToken (this.Command args)

    /// `OutputString` against `CancellationToken.None`.
    member this.OutputString(args: seq<string>) =
        this.OutputString(args, CancellationToken.None)

    /// Build the command for `args` and run it to completion, capturing stdout as raw bytes.
    member this.OutputBytes(args: seq<string>, cancellationToken: CancellationToken) =
        Runner.outputBytes config.Runner cancellationToken (this.Command args)

    /// `OutputBytes` against `CancellationToken.None`.
    member this.OutputBytes(args: seq<string>) =
        this.OutputBytes(args, CancellationToken.None)

    /// The exit code; a signal kill or timeout errors instead of inventing a sentinel.
    member this.ExitCode(args: seq<string>, cancellationToken: CancellationToken) =
        Runner.exitCode config.Runner cancellationToken (this.Command args)

    /// `ExitCode` against `CancellationToken.None`.
    member this.ExitCode(args: seq<string>) =
        this.ExitCode(args, CancellationToken.None)

    /// Read the exit code as a yes/no answer: 0 -> true, 1 -> false, anything else errors.
    member this.Probe(args: seq<string>, cancellationToken: CancellationToken) =
        Runner.probe config.Runner cancellationToken (this.Command args)

    /// `Probe` against `CancellationToken.None`.
    member this.Probe(args: seq<string>) =
        this.Probe(args, CancellationToken.None)

    /// Start the command for `args` and return a live `RunningProcess`.
    member this.Start(args: seq<string>, cancellationToken: CancellationToken) =
        Runner.start config.Runner cancellationToken (this.Command args)

    /// `Start` against `CancellationToken.None`.
    member this.Start(args: seq<string>) =
        this.Start(args, CancellationToken.None)

    /// Require a zero/accepted exit and parse the trimmed stdout into a `'T`; a thrown parser error
    /// becomes `ProcessError.Parse`.
    member this.Parse(args: seq<string>, parser: Func<string, 'T>, cancellationToken: CancellationToken) =
        ArgumentNullException.ThrowIfNull parser
        Runner.parse config.Runner cancellationToken parser.Invoke (this.Command args)

    /// `Parse` against `CancellationToken.None`.
    member this.Parse(args: seq<string>, parser: Func<string, 'T>) =
        this.Parse(args, parser, CancellationToken.None)

    /// Like `Parse`, but the parser returns its own `Result` (its error message becomes `Parse`).
    member this.TryParse
        (args: seq<string>, parser: Func<string, Result<'T, string>>, cancellationToken: CancellationToken)
        =
        ArgumentNullException.ThrowIfNull parser
        Runner.tryParse config.Runner cancellationToken parser.Invoke (this.Command args)

    /// `TryParse` against `CancellationToken.None`.
    member this.TryParse(args: seq<string>, parser: Func<string, Result<'T, string>>) =
        this.TryParse(args, parser, CancellationToken.None)

    /// The first stdout line satisfying `predicate`, or `None` if stdout closes without a match.
    member this.FirstLine(args: seq<string>, predicate: Func<string, bool>, cancellationToken: CancellationToken) =
        ArgumentNullException.ThrowIfNull predicate
        Runner.firstLine config.Runner cancellationToken predicate.Invoke (this.Command args)

    /// `FirstLine` against `CancellationToken.None`.
    member this.FirstLine(args: seq<string>, predicate: Func<string, bool>) =
        this.FirstLine(args, predicate, CancellationToken.None)

/// Pipe-friendly entry point for `CliClient`.
[<RequireQualifiedAccess>]
module CliClient =

    /// A client for `program`, run through the default `JobRunner`.
    let create (program: string) = CliClient(program)
