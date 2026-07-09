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
      // How the line-pumped path frames a captured/streamed line, per stream. The default
      // (`LineTerminator.Lf`) reproduces ProcessKit's original `\n`-splitting behaviour; the raw
      // byte path (`OutputBytesAsync`) and the tees are unaffected.
      StdoutLineTerminator: LineTerminator
      StderrLineTerminator: LineTerminator
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
      // Opt-in idle deadline: kill the run when neither stdout nor stderr produces output for this long
      // (each chunk of output resets it), independent of the total `Timeout`. `None` (the default) is no
      // idle deadline. A pipeline stage cannot honour it (rejected by `PipelineStageGuard`).
      IdleTimeout: TimeSpan option
      CancelOn: CancellationToken option
      Retry: (int * TimeSpan * Func<ProcessError, bool>) option
      // Explicit one-shot opt-out of retrying, distinct from `Retry = None` ("no policy set"). Set by
      // `RetryNever` and read by the verb layer's `withRetry`, which runs the command exactly once
      // whenever this is `true` — even if `Retry` itself carries a policy inherited from a
      // `CliClient.WithDefaults` template. `Retry` (the builder method) resets this back to `false`,
      // so the last of `.Retry(...)`/`.RetryNever()` in a chain wins, like every other builder knob.
      RetryDisabled: bool
      UncheckedInPipe: bool
      OkCodes: int list
      CreateNoWindow: bool
      // Windows: spawn the child as its own console process group (`CREATE_NEW_PROCESS_GROUP`) so
      // `ProcessGroup.Signal(Signal.Int/Term)` can deliver a best-effort console CTRL+BREAK to it
      // instead of the hard Job kill. Default `false`; no effect on Unix (which signals the child's
      // process group through `killpg` regardless).
      WindowsCtrlSignals: bool
      // The child's CPU-scheduling priority, applied at spawn (Windows priority class / Unix nice).
      // `None` (the default) leaves the OS default untouched.
      Priority: Priority option
      // The child's Unix file-mode creation mask (`umask(2)`), applied at spawn on the POSIX path.
      // `None` (the default) leaves the inherited umask untouched. Unix-only: a set value fails a
      // Windows spawn with `ProcessError.Unsupported` (there is no Windows equivalent), never a silent
      // drop. Only the low permission bits are meaningful, as with `umask(2)` itself.
      Umask: int option
      // Unix privilege drop: run the child under this user id (`setuid`) / group id (`setgid`). `None`
      // (the default) inherits the parent's ids. Unix-only: a set value fails a Windows spawn with
      // `ProcessError.Unsupported`, never a silent drop. Because `posix_spawn` exposes no uid/gid
      // attribute (and forking a managed runtime to drop in a child is unsafe on .NET), a spawn that
      // requests either is rewritten to run through the `setpriv` helper (util-linux) on the ordinary
      // `posix_spawn` path (see `Native.Posix.setprivCommand`): `setpriv` sets the gid before the uid and
      // clears the parent's supplementary groups, then `exec`s the real program. A non-root caller asking
      // for a different id is rejected up front with `ProcessError.Spawn`; a missing `setpriv` is a typed
      // `ProcessError.Spawn`.
      Uid: int option
      Gid: int option
      // Unix `setsid()`: detach the child into a brand-new session with no controlling terminal. `false`
      // (the default) leaves the child in the caller's session. Unix-only: `true` fails a Windows spawn
      // with `ProcessError.Unsupported`. `setsid()` also makes the child a new process-group leader
      // (pgid == pid == sid), so it REPLACES the group's `POSIX_SPAWN_SETPGROUP` for that command rather
      // than combining with it; the kill-on-drop `killpg(pid)` teardown still reaches the whole session,
      // so containment is preserved (see `Native.Posix`).
      Setsid: bool
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
          StdoutLineTerminator = LineTerminator.Lf
          StderrLineTerminator = LineTerminator.Lf
          OnStdoutLine = None
          OnStderrLine = None
          StdoutTee = None
          StderrTee = None
          OutputBuffer = OutputBufferPolicy.Default
          StreamBuffer = None
          Timeout = None
          TimeoutGrace = None
          IdleTimeout = None
          CancelOn = None
          Retry = None
          RetryDisabled = false
          UncheckedInPipe = false
          OkCodes = [ 0 ]
          CreateNoWindow = false
          WindowsCtrlSignals = false
          Priority = None
          Umask = None
          Uid = None
          Gid = None
          Setsid = false
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

    /// Frame captured/streamed **stdout** lines with `terminator` (default `LineTerminator.Lf` — split
    /// on `\n`). Pass `LineTerminator.Cr`/`Any` to split carriage-return progress output on a bare
    /// `\r`. Affects only the line-pumped path (streaming, per-line handlers, `OutputStringAsync`); the
    /// raw `OutputBytesAsync` bytes and the tees stay byte-exact.
    member _.StdoutLineTerminator(terminator: LineTerminator) =
        Command(
            { config with
                StdoutLineTerminator = terminator }
        )

    /// Frame captured/streamed **stderr** lines with `terminator` (default `LineTerminator.Lf`). See
    /// `StdoutLineTerminator`; the stdout framing is left untouched.
    member _.StderrLineTerminator(terminator: LineTerminator) =
        Command(
            { config with
                StderrLineTerminator = terminator }
        )

    /// Frame **both** captured/streamed streams' lines with `terminator`. See `StdoutLineTerminator`
    /// for what the line framing governs (and what it leaves byte-exact).
    member _.LineTerminator(terminator: LineTerminator) =
        Command(
            { config with
                StdoutLineTerminator = terminator
                StderrLineTerminator = terminator }
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

    /// Kill the run when it produces **no output** — on neither stdout nor stderr — for `duration`,
    /// reporting the result as `Outcome.TimedOut`. Every chunk of output resets the deadline, so a run
    /// that keeps streaming stays alive; one that hangs after going quiet is killed. This is distinct
    /// from `Timeout`, which bounds the *total* run length regardless of output: the two are
    /// independent and may both be set, each firing on its own condition. Idle activity is measured at
    /// byte granularity across every verb (buffered capture, streaming, raw bytes, and the drained
    /// `WaitAsync`/`ProfileAsync`), so even a run whose output is discarded — or a single long
    /// newline-free blob — counts as active. A negative `duration` is rejected
    /// (`ArgumentOutOfRangeException`, matching `Timeout`); one larger than ~24.8 days is treated as no
    /// idle deadline. Honours `TimeoutGrace` (a graceful stop, then a hard kill) exactly as `Timeout`.
    member _.IdleTimeout(duration: TimeSpan) =
        ArgumentOutOfRangeException.ThrowIfLessThan(duration, TimeSpan.Zero)

        Command(
            { config with
                IdleTimeout = Some duration }
        )

    /// Also cancel the run when `cancellationToken` fires (in addition to any verb token). This binds the
    /// token to the **completion** verbs — `RunAsync`/`Output*`/`ExitCodeAsync`/`ProbeAsync`/`ParseAsync`/`FirstLineAsync`:
    /// they drive the child to completion, so they watch the token for the whole run and turn a fired token
    /// into `ProcessError.Cancelled`.
    ///
    /// It does **not** reach a live `StartAsync`/`SpawnAsync` handle. On that path the verb's own token is
    /// checked exactly once, before the actual spawn (an already-cancelled token short-circuits to
    /// `ProcessError.Cancelled` and starts nothing); once the child is running, neither this `CancelOn`
    /// token nor the token passed to `StartAsync` is tracked. A live handle is caller-driven — cancel or
    /// reap it yourself: dispose it, call its `Kill`, or register your own callback on the token that calls
    /// `Kill`.
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
                Retry = Some(maxAttempts, delay, shouldRetry)
                // A fresh `Retry` call re-opts-in, undoing an earlier `RetryNever` in the same chain —
                // the last of the two builder calls wins, as with every other knob.
                RetryDisabled = false }
        )

    /// Explicitly disable retrying for this command, overriding any `Retry` policy already on it —
    /// including one inherited from a `CliClient.WithDefaults` template. Distinct from never having
    /// called `Retry` at all: an unset `Retry` still accepts a client's default, `RetryNever` refuses
    /// it. The command always runs exactly once. A later `Retry(...)` call in the same chain re-opts
    /// back in (the last call wins).
    member _.RetryNever() =
        Command({ config with RetryDisabled = true })

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

    /// Windows: spawn the child as its own console process group (`CREATE_NEW_PROCESS_GROUP`), so that
    /// `ProcessGroup.Signal(Signal.Int)` / `Signal.Term` can deliver it a best-effort console
    /// **CTRL+BREAK** — the closest Windows analogue to a graceful `SIGINT`/`SIGTERM` — instead of the
    /// hard atomic Job-Object kill, giving a console child a chance to clean up. **Best-effort and
    /// console-only:** the event reaches only a console child that shares the caller's console. A child
    /// given its own or hidden console (via `CreateNoWindow`), or a parent that has no console at all,
    /// cannot receive it — the send then fails honestly with `ProcessError.Unsupported` rather than a
    /// silent downgrade — and even on a successful send delivery is not guaranteed (the child may
    /// install its own console handler). `Signal.Kill` is unaffected (always the atomic Job kill), and
    /// this has no effect on Unix, where signals reach the child's process group regardless. Note that
    /// `CREATE_NEW_PROCESS_GROUP` also disables the child's default CTRL+C handling, which is why the
    /// soft signal is delivered as CTRL+BREAK rather than CTRL+C.
    member _.WindowsCtrlSignals() =
        Command(
            { config with
                WindowsCtrlSignals = true }
        )

    /// Launch the child — and the process tree it spawns — at a lower (or higher) CPU-scheduling
    /// `priority`: a Windows priority class set at process creation, or a Unix `nice` value applied via
    /// `setpriority`. Supported on both platforms (never `ProcessError.Unsupported`); the default
    /// (unset) leaves the OS default. Raising priority above the inherited level on Unix
    /// (`Priority.High`/`Priority.AboveNormal`) needs privilege — without it the spawn fails with
    /// `ProcessError.Spawn` rather than silently running lower. See `Priority`.
    member _.Priority(priority: Priority) =
        Command({ config with Priority = Some priority })

    /// Set the child's Unix file-mode creation mask (`umask(2)`), controlling the default permissions
    /// of files it creates — pass the value you would give the `umask` shell builtin (e.g. `0o022`).
    /// Only the low permission bits are meaningful, as with the syscall itself. **Unix-only:** on
    /// Windows (which has no equivalent) a set mask fails the spawn with `ProcessError.Unsupported`
    /// rather than being silently ignored. The default (unset) leaves the inherited umask untouched.
    /// `mask` must be within `0..0o7777` (the meaningful permission-bit range); outside it an
    /// `ArgumentOutOfRangeException` is thrown at the builder boundary rather than being handed to
    /// `umask(2)` as-is.
    member _.Umask(mask: int) =
        ArgumentOutOfRangeException.ThrowIfNegative mask
        ArgumentOutOfRangeException.ThrowIfGreaterThan(mask, 0o7777)
        Command({ config with Umask = Some mask })

    /// Run the child under this Unix user id (`setuid`). **Unix-only:** on Windows (which has no
    /// equivalent) a requested uid fails the spawn with `ProcessError.Unsupported` rather than being
    /// silently ignored. Because `posix_spawn` has no uid attribute, a command with a uid (or `Gid`) is
    /// spawned through the `setpriv` helper (util-linux), which drops the gid/uid and clears the
    /// supplementary groups before `exec`ing the real program in place. Dropping to another user needs
    /// privilege (root / `CAP_SETUID`): a non-root caller asking for a different uid fails the spawn with
    /// `ProcessError.Spawn` (never a child that kept the parent's uid), as does a host with no `setpriv`
    /// (mainstream Linux has it; macOS/BSD do not). `uid` must be non-negative (rejected with
    /// `ArgumentOutOfRangeException` at the builder boundary). Pair with `Gid` (or `User`) for a full drop.
    member _.Uid(uid: int) =
        ArgumentOutOfRangeException.ThrowIfNegative uid
        Command({ config with Uid = Some uid })

    /// Run the child under this Unix group id (`setgid`) — see `Uid` for the mechanism, platform notes,
    /// and privilege requirement. `setgid` is applied before any `setuid`, so the two compose into a
    /// correct privilege drop. `gid` must be non-negative.
    member _.Gid(gid: int) =
        ArgumentOutOfRangeException.ThrowIfNegative gid
        Command({ config with Gid = Some gid })

    /// Run the child under this Unix user **and** group id — the common privilege-drop pair, equivalent
    /// to `.Gid(gid).Uid(uid)`. See `Uid` for the mechanism, ordering (`setgid` before `setuid`),
    /// supplementary-group clearing, platform notes, and privilege requirement. Both ids must be
    /// non-negative.
    member _.User(uid: int, gid: int) =
        ArgumentOutOfRangeException.ThrowIfNegative uid
        ArgumentOutOfRangeException.ThrowIfNegative gid

        Command(
            { config with
                Uid = Some uid
                Gid = Some gid }
        )

    /// Detach the child into a **new session** (`setsid()`): its own session and process group, with no
    /// controlling terminal. **Unix-only:** on Windows a requested detach fails the spawn with
    /// `ProcessError.Unsupported`. `setsid()` makes the child a new process-group leader (pgid == pid),
    /// so the kill-on-drop group teardown (`killpg`) still reaches the whole session — containment is
    /// preserved; the new session simply replaces the group's default `POSIX_SPAWN_SETPGROUP` for this
    /// command. A `setsid()` the OS refuses fails the spawn with `ProcessError.Spawn`.
    member _.Setsid() = Command({ config with Setsid = true })

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

    /// Frame captured/streamed stdout lines with `terminator` (default `LineTerminator.Lf`).
    let stdoutLineTerminator (terminator: LineTerminator) (command: Command) = command.StdoutLineTerminator terminator

    /// Frame captured/streamed stderr lines with `terminator` (default `LineTerminator.Lf`).
    let stderrLineTerminator (terminator: LineTerminator) (command: Command) = command.StderrLineTerminator terminator

    /// Frame both captured/streamed streams' lines with `terminator` (default `LineTerminator.Lf`).
    let lineTerminator (terminator: LineTerminator) (command: Command) = command.LineTerminator terminator

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

    /// Kill the run when it produces no output (stdout or stderr) for `duration` — reset by each chunk
    /// of output — independent of the total `Command.Timeout`.
    let idleTimeout (duration: TimeSpan) (command: Command) = command.IdleTimeout duration

    /// Also cancel the run when `cancellationToken` fires.
    let cancelOn (cancellationToken: CancellationToken) (command: Command) = command.CancelOn cancellationToken

    /// Run the command up to `maxAttempts` times in total (initial run plus retries), waiting `delay`
    /// between attempts (`0`/`1` both mean a single run).
    let retry (maxAttempts: int) (delay: TimeSpan) (shouldRetry: ProcessError -> bool) (command: Command) =
        command.Retry(maxAttempts, delay, Func<ProcessError, bool> shouldRetry)

    /// Explicitly disable retrying for this command, overriding any inherited `Retry` policy (e.g.
    /// from a `CliClient.WithDefaults` template). The command always runs exactly once.
    let retryNever (command: Command) = command.RetryNever()

    /// Inside a pipeline, allow this stage to exit non-zero without failing the pipeline.
    let uncheckedInPipe (command: Command) = command.UncheckedInPipe()

    /// Replace the success exit-code set with these codes (default `{0}`; include `0` to keep it; an
    /// empty set is a no-op that keeps the previously configured codes).
    let okCodes (codes: seq<int>) (command: Command) = command.OkCodes codes

    /// Windows: run the child with `CREATE_NO_WINDOW` (no effect on Unix).
    let createNoWindow (command: Command) = command.CreateNoWindow()

    /// Windows: spawn the child as its own console process group so `ProcessGroup.Signal(Signal.Int/Term)`
    /// can deliver a best-effort CTRL+BREAK (no effect on Unix). See `Command.WindowsCtrlSignals`.
    let windowsCtrlSignals (command: Command) = command.WindowsCtrlSignals()

    /// Launch the child (and its spawned tree) at a lower/higher CPU-scheduling priority (Windows
    /// priority class / Unix nice). Supported on both platforms; the default leaves the OS default.
    let priority (level: Priority) (command: Command) = command.Priority level

    /// Set the child's Unix file-mode creation mask (`umask(2)`). Unix-only: a set mask fails a Windows
    /// spawn with `ProcessError.Unsupported`. The default leaves the inherited umask untouched.
    let umask (mask: int) (command: Command) = command.Umask mask

    /// Run the child under this Unix user id (`setuid`). Unix-only: a set uid fails a Windows spawn with
    /// `ProcessError.Unsupported`; dropping needs privilege (else `ProcessError.Spawn`). See `Command.Uid`.
    let uid (value: int) (command: Command) = command.Uid value

    /// Run the child under this Unix group id (`setgid`). Unix-only, same notes as `uid`. See `Command.Gid`.
    let gid (value: int) (command: Command) = command.Gid value

    /// Run the child under this Unix user and group id (the privilege-drop pair). See `Command.User`.
    let user (uid: int) (gid: int) (command: Command) = command.User(uid, gid)

    /// Detach the child into a new session (`setsid()`). Unix-only: a set request fails a Windows spawn
    /// with `ProcessError.Unsupported`. Containment is preserved. See `Command.Setsid`.
    let setsid (command: Command) = command.Setsid()

    /// Emit structured lifecycle events to `logger` (argv/env never logged).
    let logger (logger: ILogger) (command: Command) = command.Logger logger
