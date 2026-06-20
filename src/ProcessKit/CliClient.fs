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

    /// Build the command for `args` and run it (requiring a zero/accepted exit), returning trimmed
    /// stdout.
    member this.Run(args: seq<string>) =
        Runner.run config.Runner CancellationToken.None (this.Command args)

    /// Build the command for `args` and run it to completion (a non-zero exit is data).
    member this.OutputString(args: seq<string>) =
        Runner.outputString config.Runner CancellationToken.None (this.Command args)

    /// Build the command for `args` and run it to completion, capturing stdout as raw bytes.
    member this.OutputBytes(args: seq<string>) =
        Runner.outputBytes config.Runner CancellationToken.None (this.Command args)

/// Pipe-friendly entry point for `CliClient`.
[<RequireQualifiedAccess>]
module CliClient =

    /// A client for `program`, run through the default `JobRunner`.
    let create (program: string) = CliClient(program)
