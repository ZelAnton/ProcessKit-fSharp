namespace ProcessKit

open System
open System.Collections.Generic

/// An immutable description of a process to run.
///
/// Build it fluently — each method returns a new `Command`. The value is the *cold*
/// description of a run; the process is launched only when a verb (e.g. `Runner.run`) is
/// invoked. Use the instance methods (`cmd.Arg "x"`) or the `Command` module's
/// pipe-friendly functions (`cmd |> Command.arg "x"`).
[<Sealed>]
type Command
    private
    (
        program: string,
        args: string list,
        workingDirectory: string option,
        envOverrides: (string * string option) list,
        clearEnv: bool
    ) =

    /// Start a new command for the given program (resolved on PATH unless a path is given).
    new(program: string) =
        ArgumentNullException.ThrowIfNull program
        Command(program, [], None, [], false)

    /// The program to run.
    member _.Program = program

    /// The arguments, in order.
    member _.Arguments: IReadOnlyList<string> = List.toArray args

    /// The working directory, when overridden.
    member _.WorkingDirectory = workingDirectory

    /// Environment overrides applied over the inherited environment, in order
    /// (`Some` sets a variable, `None` removes it).
    member internal _.EnvOverrides = envOverrides

    /// True when the child's environment starts empty instead of inheriting the parent's.
    member internal _.ClearEnv = clearEnv

    /// Append a single argument.
    member _.Arg(value: string) =
        ArgumentNullException.ThrowIfNull value
        Command(program, args @ [ value ], workingDirectory, envOverrides, clearEnv)

    /// Append several arguments, in order.
    member _.Args(values: seq<string>) =
        ArgumentNullException.ThrowIfNull values
        Command(program, args @ List.ofSeq values, workingDirectory, envOverrides, clearEnv)

    /// Set the working directory for the run.
    member _.CurrentDir(directory: string) =
        ArgumentNullException.ThrowIfNull directory
        Command(program, args, Some directory, envOverrides, clearEnv)

    /// Set an environment variable for the child.
    member _.Env(key: string, value: string) =
        ArgumentNullException.ThrowIfNull key
        ArgumentNullException.ThrowIfNull value
        Command(program, args, workingDirectory, envOverrides @ [ key, Some value ], clearEnv)

    /// Remove an inherited environment variable from the child.
    member _.EnvRemove(key: string) =
        ArgumentNullException.ThrowIfNull key
        Command(program, args, workingDirectory, envOverrides @ [ key, None ], clearEnv)

    /// Start the child's environment empty instead of inheriting the parent's.
    member _.EnvClear() =
        Command(program, args, workingDirectory, envOverrides, true)

/// Pipe-friendly functions over `Command`, mirroring the instance methods.
[<RequireQualifiedAccess>]
module Command =

    /// Create a command for the given program.
    let create (program: string) = Command(program)

    /// Append a single argument.
    let arg (value: string) (command: Command) = command.Arg value

    /// Append several arguments, in order.
    let args (values: seq<string>) (command: Command) = command.Args values

    /// Set the working directory for the run.
    let currentDir (directory: string) (command: Command) = command.CurrentDir directory

    /// Set an environment variable for the child.
    let env (key: string) (value: string) (command: Command) = command.Env(key, value)

    /// Remove an inherited environment variable from the child.
    let envRemove (key: string) (command: Command) = command.EnvRemove key

    /// Start the child's environment empty instead of inheriting the parent's.
    let envClear (command: Command) = command.EnvClear()
