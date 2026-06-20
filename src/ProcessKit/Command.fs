namespace ProcessKit

open System
open System.Collections.Generic
open System.IO
open System.Text
open System.Threading
open Microsoft.Extensions.Logging

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
      OutputBuffer: OutputBufferPolicy
      Timeout: TimeSpan option
      TimeoutGrace: TimeSpan option
      CancelOn: CancellationToken option
      Retry: (int * TimeSpan * Func<ProcessError, bool>) option
      UncheckedInPipe: bool
      OkCodes: int list
      CreateNoWindow: bool
      Logger: ILogger option }

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
          OutputBuffer = OutputBufferPolicy.Default
          Timeout = None
          TimeoutGrace = None
          CancelOn = None
          Retry = None
          UncheckedInPipe = false
          OkCodes = [ 0 ]
          CreateNoWindow = false
          Logger = None }

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

    /// The configured run timeout, if any.
    member _.ConfiguredTimeout = config.Timeout

    /// Kill the run after `duration`, reporting the result as `Outcome.TimedOut`.
    member _.Timeout(duration: TimeSpan) =
        Command({ config with Timeout = Some duration })

    /// On timeout, terminate gracefully (SIGTERM) and force-kill only if still alive after
    /// `grace`. On Windows this degrades to the atomic Job kill.
    member _.TimeoutGrace(grace: TimeSpan) =
        Command(
            { config with
                TimeoutGrace = Some grace }
        )

    /// Also cancel the run when `cancellationToken` fires (in addition to any verb token).
    member _.CancelOn(cancellationToken: CancellationToken) =
        Command(
            { config with
                CancelOn = Some cancellationToken }
        )

    /// Retry a failed run up to `maxAttempts` extra times, waiting `delay` between attempts, while
    /// `shouldRetry` returns true for the error.
    member _.Retry(maxAttempts: int, delay: TimeSpan, shouldRetry: Func<ProcessError, bool>) =
        ArgumentNullException.ThrowIfNull shouldRetry

        Command(
            { config with
                Retry = Some(maxAttempts, delay, shouldRetry) }
        )

    /// Inside a pipeline, do not let this stage's non-zero exit fail the pipeline (it is still
    /// reported in the stage outcomes). Outside a pipeline this flag has no effect.
    member _.UncheckedInPipe() =
        Command({ config with UncheckedInPipe = true })

    /// Treat these exit codes (in addition to `0`) as success — widening `ProcessResult.IsSuccess`,
    /// `ensureSuccess`, and the `Run` verbs. An empty set resets to the default `{0}`.
    member _.OkCodes(codes: seq<int>) =
        ArgumentNullException.ThrowIfNull codes
        let list = List.ofSeq codes

        Command(
            { config with
                OkCodes = (if List.isEmpty list then [ 0 ] else list) }
        )

    /// Windows: run the child with `CREATE_NO_WINDOW`, so a console child spawned from a GUI app
    /// does not flash a console window. No effect on Unix.
    member _.CreateNoWindow() =
        Command({ config with CreateNoWindow = true })

    /// Emit structured lifecycle events (spawn / exit / timeout / retry) to `logger`. The program
    /// name and non-secret facts only — **argv and environment are never logged**.
    member _.WithLogger(logger: ILogger) =
        ArgumentNullException.ThrowIfNull logger
        Command({ config with Logger = Some logger })

    /// Inherit the parent's environment (the default). `InheritEnv false` starts the child's
    /// environment empty — the same as `EnvClear`.
    member this.InheritEnv(inheritEnv: bool) =
        if inheritEnv then
            Command({ config with ClearEnv = false })
        else
            Command({ config with ClearEnv = true })

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

    /// Kill the run after `duration`.
    let timeout (duration: TimeSpan) (command: Command) = command.Timeout duration

    /// Terminate gracefully on timeout, force-killing only after `grace`.
    let timeoutGrace (grace: TimeSpan) (command: Command) = command.TimeoutGrace grace

    /// Also cancel the run when `cancellationToken` fires.
    let cancelOn (cancellationToken: CancellationToken) (command: Command) = command.CancelOn cancellationToken

    /// Retry a failed run up to `maxAttempts` extra times, waiting `delay` between attempts.
    let retry (maxAttempts: int) (delay: TimeSpan) (shouldRetry: ProcessError -> bool) (command: Command) =
        command.Retry(maxAttempts, delay, Func<ProcessError, bool> shouldRetry)

    /// Inside a pipeline, allow this stage to exit non-zero without failing the pipeline.
    let uncheckedInPipe (command: Command) = command.UncheckedInPipe()

    /// Treat these exit codes (in addition to `0`) as success.
    let okCodes (codes: seq<int>) (command: Command) = command.OkCodes codes

    /// Windows: run the child with `CREATE_NO_WINDOW` (no effect on Unix).
    let createNoWindow (command: Command) = command.CreateNoWindow()

    /// Inherit the parent's environment (the default); `false` starts it empty.
    let inheritEnv (inheritEnv: bool) (command: Command) = command.InheritEnv inheritEnv

    /// Emit structured lifecycle events to `logger` (argv/env never logged).
    let withLogger (logger: ILogger) (command: Command) = command.WithLogger logger
