namespace ProcessKit

open System
open System.Collections.Generic
open System.IO
open System.Text

/// The immutable configuration behind a `Command`. Internal — consumers build it through the
/// `Command` builder; the runner/native layer reads it to spawn.
type internal CommandConfig =
    { Program: string
      Args: string list
      WorkingDirectory: string option
      EnvOverrides: (string * string option) list
      ClearEnv: bool
      StdinSource: Stdin option
      KeepStdinOpen: bool
      StdoutMode: StdioMode
      StderrMode: StdioMode
      StdoutEncoding: Encoding
      StderrEncoding: Encoding
      OnStdoutLine: Action<string> option
      OnStderrLine: Action<string> option
      StdoutTee: Stream option
      StderrTee: Stream option
      OutputBuffer: OutputBufferPolicy }

module internal CommandConfig =

    let create (program: string) =
        { Program = program
          Args = []
          WorkingDirectory = None
          EnvOverrides = []
          ClearEnv = false
          StdinSource = None
          KeepStdinOpen = false
          StdoutMode = StdioMode.Piped
          StderrMode = StdioMode.Piped
          StdoutEncoding = Encoding.UTF8
          StderrEncoding = Encoding.UTF8
          OnStdoutLine = None
          OnStderrLine = None
          StdoutTee = None
          StderrTee = None
          OutputBuffer = OutputBufferPolicy.Default }

/// An immutable description of a process to run.
///
/// Build it fluently — each method returns a new `Command`. The value is the *cold* description
/// of a run; the process is launched only when a verb (`Runner.run`, `Command.start`, …) is
/// invoked. Use the instance methods (`cmd.Arg "x"`) or the `Command` module's pipe-friendly
/// functions (`cmd |> Command.arg "x"`).
[<Sealed>]
type Command internal (config: CommandConfig) =

    /// Start a new command for the given program (resolved on PATH unless a path is given).
    new(program: string) =
        ArgumentNullException.ThrowIfNull program
        Command(CommandConfig.create program)

    member internal _.Config = config

    /// The program to run.
    member _.Program = config.Program

    /// The arguments, in order.
    member _.Arguments: IReadOnlyList<string> = List.toArray config.Args

    /// The working directory, when overridden.
    member _.WorkingDirectory = config.WorkingDirectory

    /// Append a single argument.
    member _.Arg(value: string) =
        ArgumentNullException.ThrowIfNull value

        Command(
            { config with
                Args = config.Args @ [ value ] }
        )

    /// Append several arguments, in order.
    member _.Args(values: seq<string>) =
        ArgumentNullException.ThrowIfNull values

        Command(
            { config with
                Args = config.Args @ List.ofSeq values }
        )

    /// Set the working directory for the run.
    member _.CurrentDir(directory: string) =
        ArgumentNullException.ThrowIfNull directory

        Command(
            { config with
                WorkingDirectory = Some directory }
        )

    /// Set an environment variable for the child.
    member _.Env(key: string, value: string) =
        ArgumentNullException.ThrowIfNull key
        ArgumentNullException.ThrowIfNull value

        Command(
            { config with
                EnvOverrides = config.EnvOverrides @ [ key, Some value ] }
        )

    /// Remove an inherited environment variable from the child.
    member _.EnvRemove(key: string) =
        ArgumentNullException.ThrowIfNull key

        Command(
            { config with
                EnvOverrides = config.EnvOverrides @ [ key, None ] }
        )

    /// Start the child's environment empty instead of inheriting the parent's.
    member _.EnvClear() =
        Command({ config with ClearEnv = true })

    /// Feed the child's standard input from `source`.
    member _.Stdin(source: Stdin) =
        ArgumentNullException.ThrowIfNull source

        Command(
            { config with
                StdinSource = Some source }
        )

    /// Keep the child's stdin pipe open after the source is exhausted (for interactive writing
    /// via `RunningProcess.TakeStdin`).
    member _.KeepStdinOpen() =
        Command({ config with KeepStdinOpen = true })

    /// Set how the child's standard output is connected (default `Piped`).
    member _.Stdout(mode: StdioMode) =
        Command({ config with StdoutMode = mode })

    /// Set how the child's standard error is connected (default `Piped`).
    member _.Stderr(mode: StdioMode) =
        Command({ config with StderrMode = mode })

    /// Decode captured stdout with `encoding` (default UTF-8).
    member _.StdoutEncoding(encoding: Encoding) =
        ArgumentNullException.ThrowIfNull encoding

        Command(
            { config with
                StdoutEncoding = encoding }
        )

    /// Decode captured stderr with `encoding` (default UTF-8).
    member _.StderrEncoding(encoding: Encoding) =
        ArgumentNullException.ThrowIfNull encoding

        Command(
            { config with
                StderrEncoding = encoding }
        )

    /// Decode both captured streams with `encoding`.
    member _.Encoding(encoding: Encoding) =
        ArgumentNullException.ThrowIfNull encoding

        Command(
            { config with
                StdoutEncoding = encoding
                StderrEncoding = encoding }
        )

    /// Invoke `handler` for each captured stdout line, as it is pumped.
    member _.OnStdoutLine(handler: Action<string>) =
        ArgumentNullException.ThrowIfNull handler

        Command(
            { config with
                OnStdoutLine = Some handler }
        )

    /// Invoke `handler` for each captured stderr line, as it is pumped.
    member _.OnStderrLine(handler: Action<string>) =
        ArgumentNullException.ThrowIfNull handler

        Command(
            { config with
                OnStderrLine = Some handler }
        )

    /// Copy raw captured stdout bytes to `sink` (a tee), in addition to capture.
    member _.StdoutTee(sink: Stream) =
        ArgumentNullException.ThrowIfNull sink
        Command({ config with StdoutTee = Some sink })

    /// Copy raw captured stderr bytes to `sink` (a tee), in addition to capture.
    member _.StderrTee(sink: Stream) =
        ArgumentNullException.ThrowIfNull sink
        Command({ config with StderrTee = Some sink })

    /// Bound the in-memory backlog of captured lines.
    member _.OutputBuffer(policy: OutputBufferPolicy) =
        ArgumentNullException.ThrowIfNull policy
        Command({ config with OutputBuffer = policy })

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

    /// Feed the child's standard input from `source`.
    let stdin (source: Stdin) (command: Command) = command.Stdin source

    /// Keep the child's stdin pipe open after the source is exhausted.
    let keepStdinOpen (command: Command) = command.KeepStdinOpen()

    /// Set how the child's standard output is connected.
    let stdout (mode: StdioMode) (command: Command) = command.Stdout mode

    /// Set how the child's standard error is connected.
    let stderr (mode: StdioMode) (command: Command) = command.Stderr mode

    /// Decode both captured streams with `encoding`.
    let encoding (enc: Encoding) (command: Command) = command.Encoding enc

    /// Invoke `handler` for each captured stdout line.
    let onStdoutLine (handler: string -> unit) (command: Command) =
        command.OnStdoutLine(Action<string> handler)

    /// Invoke `handler` for each captured stderr line.
    let onStderrLine (handler: string -> unit) (command: Command) =
        command.OnStderrLine(Action<string> handler)

    /// Copy raw captured stdout bytes to `sink`.
    let stdoutTee (sink: Stream) (command: Command) = command.StdoutTee sink

    /// Copy raw captured stderr bytes to `sink`.
    let stderrTee (sink: Stream) (command: Command) = command.StderrTee sink

    /// Bound the in-memory backlog of captured lines.
    let outputBuffer (policy: OutputBufferPolicy) (command: Command) = command.OutputBuffer policy
