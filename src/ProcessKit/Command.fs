namespace ProcessKit

open System
open System.Collections.Generic
open System.Collections.Immutable
open System.IO
open System.Text
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Logging

/// The immutable configuration behind a `Command`. Internal — consumers build it through the
/// `Command` builder; the runner/native layer reads it to spawn.
///
/// `Args`/`EnvOverrides` are `ImmutableList<'T>` (an AVL-tree-backed persistent list), not `'T list`
/// — a long `.Arg(x)`/`.Env(k, v)` chain calls `Add`/`AddRange` in O(log n) per call (O(n log n)
/// total), instead of the O(n) `@`/list-append that a plain F# list would need for each appended
/// element (O(n²) total on a long chain). Readers still see the same forward (append) order; they
/// enumerate/convert via `Seq.*`/`ImmutableList.ToArray()` instead of `List.*`/`::`.
type internal CommandConfig =
    { Program: string
      Args: ImmutableList<string>
      WorkingDirectory: string option
      EnvOverrides: ImmutableList<string * string option>
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
      // Opt-in bounded/backpressure policy for the streaming verbs (`StdoutLinesAsync`/
      // `OutputEventsAsync`/`WaitForLineAsync`). `None` (the default) keeps the unbounded streaming
      // channels ProcessKit has always used.
      StreamBuffer: StreamBufferPolicy option
      Timeout: TimeSpan option
      TimeoutGrace: TimeSpan option
      CancelOn: CancellationToken option
      Retry: (int * TimeSpan * Func<ProcessError, bool>) option
      UncheckedInPipe: bool
      OkCodes: int list
      CreateNoWindow: bool
      Logger: ILogger option
      // A per-run correlation id, stamped once at the verb layer so a run's log/trace events (and its
      // retries) share it. `None` until stamped; a direct spawn gets a per-incarnation id instead.
      RunId: string option }

module internal CommandConfig =

    let create (program: string) =
        { Program = program
          Args = ImmutableList<string>.Empty
          WorkingDirectory = None
          EnvOverrides = ImmutableList<string * string option>.Empty
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
          StreamBuffer = None
          Timeout = None
          TimeoutGrace = None
          CancelOn = None
          Retry = None
          UncheckedInPipe = false
          OkCodes = [ 0 ]
          CreateNoWindow = false
          Logger = None
          RunId = None }

    /// Validate an environment-variable key for `Command.Env`/`Command.EnvRemove`: must be non-empty
    /// and must not contain `=` (an env var name can never contain one; a key that did would corrupt
    /// the child's environment block, since `KEY=VALUE` is the wire format on every platform).
    let validateEnvKey (key: string) =
        if key.Length = 0 then
            raise (ArgumentException("an environment variable key must not be empty", nameof key))
        elif key.Contains '=' then
            raise (ArgumentException("an environment variable key must not contain '='", nameof key))

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
    member _.Arguments: IReadOnlyList<string> = config.Args :> IReadOnlyList<string>

    /// The working directory, when overridden.
    member _.WorkingDirectory = config.WorkingDirectory

    /// Append a single argument.
    member _.Arg(value: string) =
        ArgumentNullException.ThrowIfNull value

        Command(
            { config with
                Args = config.Args.Add value }
        )

    /// Append several arguments, in order.
    member _.Args(values: seq<string>) =
        ArgumentNullException.ThrowIfNull values

        Command(
            { config with
                Args = config.Args.AddRange values }
        )

    /// Set the working directory for the run.
    member _.CurrentDir(directory: string) =
        ArgumentNullException.ThrowIfNull directory

        Command(
            { config with
                WorkingDirectory = Some directory }
        )

    /// Set an environment variable for the child. `key` must be non-empty and must not contain `=`
    /// (rejected with `ArgumentException` — either would corrupt the child's environment block).
    member _.Env(key: string, value: string) =
        ArgumentNullException.ThrowIfNull key
        ArgumentNullException.ThrowIfNull value
        CommandConfig.validateEnvKey key

        Command(
            { config with
                EnvOverrides = config.EnvOverrides.Add(key, Some value) }
        )

    /// Remove an inherited environment variable from the child. `key` must be non-empty and must not
    /// contain `=` (same rule as `Env`).
    member _.EnvRemove(key: string) =
        ArgumentNullException.ThrowIfNull key
        CommandConfig.validateEnvKey key

        Command(
            { config with
                EnvOverrides = config.EnvOverrides.Add(key, None) }
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

    /// Opt in to a bounded/backpressure channel for the streaming verbs (`StdoutLinesAsync`/
    /// `OutputEventsAsync`/`WaitForLineAsync`). Unset (the default) keeps the unbounded streaming
    /// channel ProcessKit has always used. See [Streaming](../../docs/streaming.md) for the
    /// backpressure deadlock footgun before opting in to `StreamFullMode.Backpressure`.
    member _.StreamBuffer(policy: StreamBufferPolicy) =
        ArgumentNullException.ThrowIfNull policy

        Command(
            { config with
                StreamBuffer = Some policy }
        )

    /// The configured run timeout, if any.
    member _.ConfiguredTimeout = config.Timeout

    /// Kill the run after `duration`, reporting the result as `Outcome.TimedOut`. A negative
    /// `duration` is rejected; one larger than ~24.8 days is treated as no timeout.
    member _.Timeout(duration: TimeSpan) =
        ArgumentOutOfRangeException.ThrowIfLessThan(duration, TimeSpan.Zero)
        Command({ config with Timeout = Some duration })

    /// On timeout, terminate gracefully (SIGTERM) and force-kill only if still alive after
    /// `grace`. On Windows this degrades to the atomic Job kill. A negative `grace` is rejected
    /// (`ArgumentOutOfRangeException`), matching `Timeout`.
    member _.TimeoutGrace(grace: TimeSpan) =
        ArgumentOutOfRangeException.ThrowIfLessThan(grace, TimeSpan.Zero)

        Command(
            { config with
                TimeoutGrace = Some grace }
        )

    /// Also cancel the run when `cancellationToken` fires (in addition to any verb token). Applies to
    /// the run/capture/completion verbs (`RunAsync`/`Output*`/`ExitCodeAsync`/`ProbeAsync`/`ParseAsync`/`FirstLineAsync`); the
    /// live `StartAsync` handle is driven by its caller, so it observes only the token passed to `StartAsync`.
    member _.CancelOn(cancellationToken: CancellationToken) =
        Command(
            { config with
                CancelOn = Some cancellationToken }
        )

    /// Run the command up to `maxAttempts` times **in total** (the initial run plus up to
    /// `maxAttempts - 1` retries), waiting `delay` between attempts, while `shouldRetry` returns true
    /// for the error. `maxAttempts` of `0` or `1` both mean a single run — a command always runs at
    /// least once.
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

    /// Replace the set of exit codes treated as success (the default is `{0}`) — this is what
    /// `ProcessResult.IsSuccess`, `ensureSuccess`, and the `RunAsync` verbs check. The codes *replace* the
    /// default rather than adding to it, so pass `[0; 3]` to accept both `0` and `3`. An empty set is a
    /// **no-op** — the previously configured codes are kept (it never resets the accepted set), so it
    /// can't accidentally clear a caller's `[0; 3]` down to nothing.
    member _.OkCodes(codes: seq<int>) =
        ArgumentNullException.ThrowIfNull codes
        let list = List.ofSeq codes

        Command(
            { config with
                OkCodes = (if List.isEmpty list then config.OkCodes else list) }
        )

    /// Windows: run the child with `CREATE_NO_WINDOW`, so a console child spawned from a GUI app
    /// does not flash a console window. No effect on Unix.
    member _.CreateNoWindow() =
        Command({ config with CreateNoWindow = true })

    /// Emit structured lifecycle events (spawn / exit / timeout / retry) to `logger`. The program
    /// name and non-secret facts only — **argv and environment are never logged**.
    member _.Logger(logger: ILogger) =
        ArgumentNullException.ThrowIfNull logger
        Command({ config with Logger = Some logger })

    /// Stamp a per-run correlation id, shared by the run's log/trace events and its retries. Internal:
    /// the verb layer sets it once per logical run; a direct spawn falls back to a per-incarnation id.
    member internal _.WithRunId(runId: string) =
        Command({ config with RunId = Some runId })

/// Pipe-friendly functions over `Command`, mirroring the instance **builder** methods. The run
/// verbs (`RunAsync`/`OutputStringAsync`/`ParseAsync`/…) are instance methods only — end a pipeline with method
/// syntax (`(cmd |> Command.arg "x").RunAsync()`), or go through `Runner.*` with an explicit runner.
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

    /// Decode captured stdout with `encoding`.
    let stdoutEncoding (enc: Encoding) (command: Command) = command.StdoutEncoding enc

    /// Decode captured stderr with `encoding`.
    let stderrEncoding (enc: Encoding) (command: Command) = command.StderrEncoding enc

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

    /// Opt in to a bounded/backpressure channel for the streaming verbs (default stays unbounded).
    let streamBuffer (policy: StreamBufferPolicy) (command: Command) = command.StreamBuffer policy

    /// Kill the run after `duration`.
    let timeout (duration: TimeSpan) (command: Command) = command.Timeout duration

    /// Terminate gracefully on timeout, force-killing only after `grace`.
    let timeoutGrace (grace: TimeSpan) (command: Command) = command.TimeoutGrace grace

    /// Also cancel the run when `cancellationToken` fires.
    let cancelOn (cancellationToken: CancellationToken) (command: Command) = command.CancelOn cancellationToken

    /// Run the command up to `maxAttempts` times in total (initial run plus retries), waiting `delay`
    /// between attempts (`0`/`1` both mean a single run).
    let retry (maxAttempts: int) (delay: TimeSpan) (shouldRetry: ProcessError -> bool) (command: Command) =
        command.Retry(maxAttempts, delay, Func<ProcessError, bool> shouldRetry)

    /// Inside a pipeline, allow this stage to exit non-zero without failing the pipeline.
    let uncheckedInPipe (command: Command) = command.UncheckedInPipe()

    /// Replace the success exit-code set with these codes (default `{0}`; include `0` to keep it; an
    /// empty set is a no-op that keeps the previously configured codes).
    let okCodes (codes: seq<int>) (command: Command) = command.OkCodes codes

    /// Windows: run the child with `CREATE_NO_WINDOW` (no effect on Unix).
    let createNoWindow (command: Command) = command.CreateNoWindow()

    /// Emit structured lifecycle events to `logger` (argv/env never logged).
    let logger (logger: ILogger) (command: Command) = command.Logger logger
