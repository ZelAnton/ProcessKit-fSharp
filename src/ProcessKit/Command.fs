namespace ProcessKit

open System
open System.Collections.Generic
open System.Collections.Immutable
open System.IO
open System.Text
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Logging

/// Initial terminal geometry and behaviour flags for an opt-in pseudo-terminal (PTY) run — see
/// `Command.Pty`. A PTY gives the child a real controlling terminal (`isatty` true) on a single
/// merged stdout+stderr stream, for tools that demand a tty (an interactive `ssh`/`sudo` prompt, a
/// credential helper, a TUI, a progress bar that switches to "dumb" line-buffered output when it
/// detects a pipe). The default (no PTY) is byte-identical to a plain pipe run.
///
/// **Secret-safety (echo footgun).** A terminal echoes typed input back into its *output* by default
/// (cooked-mode `ECHO`), so bytes written to the child's stdin through the PTY — including an
/// interactively typed password — are echoed into the captured merged output. This is standard
/// terminal behaviour, not a bug, but it means a credential can appear in captured output (or a
/// recorded cassette). Set `Echo = false` to disable the terminal echo: on **POSIX** ProcessKit clears
/// the pty slave's cooked-mode `ECHO` bit (`termios`) before the child adopts it, so a password typed to
/// the child through the PTY is not echoed into the captured merged stream (proven by test). On
/// **Windows** the echo of a ConPTY is governed by the child's own console mode
/// (`ENABLE_ECHO_INPUT`/`ENABLE_LINE_INPUT` on `CONIN$`), which has no supported parent-side pre-spawn
/// override; ProcessKit therefore does not force echo off there — a documented platform divergence, not
/// a silent claim (an interactive prompt on Windows should suppress its own echo, as `ssh`/credential
/// helpers do). As everywhere else in the library, argv and environment **values** — and any PTY
/// credentials — are never logged or traced; the record/replay redaction hook still governs what a
/// cassette persists.
[<NoComparison>]
type PtyConfig =
    {
        /// Initial terminal width in columns. Must be positive (the ratified default is 80).
        Cols: int
        /// Initial terminal height in rows. Must be positive (the ratified default is 24).
        Rows: int
        /// Leave the terminal's cooked-mode echo on (`true`, the OS default) or disable it (`false`).
        /// When `false`, POSIX clears the pty slave's `termios` `ECHO` bit at spawn so typed input (e.g. a
        /// password) is not echoed into the captured merged output — see the type-level secret-safety note.
        Echo: bool
    }

    /// The ratified default PTY geometry and flags: 80 columns × 24 rows, cooked-mode echo on.
    static member Default = { Cols = 80; Rows = 24; Echo = true }

/// The Windows *mandatory integrity level* a child's token is lowered to — see
/// `Command.WindowsIntegrityLevel`.
///
/// Windows labels every token (and every securable object) with an integrity level and enforces a
/// no-write-up policy: a process may not modify an object labelled above its own level, whatever the
/// DACL says. Lowering the level a child runs at therefore takes away write access to the user's own
/// files, registry, and windows *without* changing who the child runs as — the closest Windows
/// analogue to the Unix `Uid`/`Gid` drop, and the mechanism behind browser renderer sandboxes.
///
/// Only levels at or below an ordinary process's own are offered: a token's integrity can be lowered
/// but never raised (`SetTokenInformation` refuses), so an "elevate me" variant could only ever fail
/// the spawn. Pair with `Command.WindowsRestrictedToken` for the full drop — the two are independent
/// (integrity governs what may be *written to*, privileges govern what may be *done*).
[<RequireQualifiedAccess; NoComparison>]
type WindowsIntegrityLevel =

    /// `S-1-16-8192` — the level an ordinary, non-elevated user process already runs at. Meaningful
    /// mainly from an **elevated** parent, where it drops the child back to the level the logged-on
    /// user's own programs run at instead of inheriting the parent's High integrity.
    | Medium

    /// `S-1-16-4096` — the sandbox level: no write access to the user's profile, `HKCU`, or the
    /// desktop's medium-integrity windows. A child at this level can still read most of the file
    /// system and open network connections; it writes only where a low-integrity label allows (its
    /// own `%TEMP%\Low`, for instance). The usual choice for untrusted work that must still produce
    /// output through the pipes ProcessKit already opened for it (inherited handles are unaffected —
    /// the access check happened when the parent opened them).
    | Low

    /// `S-1-16-0` — the most restrictive label Windows has: below even anonymous access, with no
    /// write access anywhere by default. Many programs cannot start at all here (a runtime that must
    /// write a temp file, load a user-profile DLL, or open a named object will fail), so treat it as
    /// a deliberate, tested choice for a self-contained binary rather than a stricter default. It is
    /// never a silent downgrade: whatever the child can no longer do surfaces as that child's own
    /// failure, not as a ProcessKit error.
    | Untrusted

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
      // Windows-only command-line fragments appended after every ordinary argument without quoting.
      // Deliberately separate from argv: callers opt into native parser-specific syntax.
      WindowsRawArgs: ImmutableList<string>
      WorkingDirectory: string option
      // Priority directories searched (in the order they were added) BEFORE `PATH` when resolving a
      // bare-name program — see `Command.PreferLocal`. A match here is always launched by its resolved
      // ABSOLUTE path (the OS never searches these directories itself, on any platform). Empty (the
      // default) is the ordinary PATH-only resolution, byte-for-byte as before. A relative entry
      // resolves against `WorkingDirectory` when one is set. `ImmutableList` (like `Args`) so a
      // repeated `.PreferLocal(dir)` chain appends in O(log n) while readers see forward (added) order.
      PreferLocal: ImmutableList<string>
      EnvOverrides: ImmutableList<string * string option>
      ClearEnv: bool
      StdinSource: Stdin option
      // The run-level hold on this command's one-shot stdin payload (`FromStream`/`FromLines`/
      // `FromAsyncLines`), taken by `Runner.withRetry` for a run that may attempt the command more than
      // once and carried here so each attempt's launch boundary can ask that hold for the loan on the
      // payload instead of being refused as a second consumer of what its own run holds
      // (`OneShotStdin.reserveLaunch`). Set only by that internal stamp — never by the public builder —
      // so an ordinary command carries `None` and every launch reserves the payload for itself.
      //
      // Carrying it exempts nothing: a command is a value that outlives its run and travels through any
      // `IProcessRunner` a caller cares to write, so the loan it asks for is exclusive, refused over a
      // payload some child has already read, and refused once the run has settled its hold — a second
      // launch bearing this stamp is checked exactly like a launch bearing none.
      StdinReservation: OneShotStdinReservation option
      KeepStdinOpen: bool
      // The encoding for text sent through `Stdin.FromString`/`FromLines`/`FromAsyncLines` and an
      // interactive `ProcessStdin.WriteLineAsync`. Raw-byte stdin remains byte-exact.
      StdinEncoding: Encoding
      // The clock and timer source for retry delays, readiness probes, PtySession pattern deadlines,
      // and supervision. The system provider is the ordinary production default; callers can supply a
      // deterministic provider for tests without changing global time.
      TimeProvider: TimeProvider
      StdoutMode: StdioMode
      StderrMode: StdioMode
      // Opt-in direct redirect of the child's stdout/stderr straight to a file at the OS level, handed to
      // the child as its std handle/fd ON THE SPAWN (Windows: an inheritable file handle in `STARTUPINFO`;
      // POSIX: a file fd via a spawn file action) — zero parent-side copying and no parent pump, so the
      // file keeps growing even after the parent (or a pump that would have drained a pipe) is gone. Each
      // is `(path, append)`: `append = false` creates/truncates, `true` appends. `None` (the default) is
      // the ordinary pipe/inherit/null wiring of `StdoutMode`/`StderrMode`. When set it takes precedence
      // over the matching `StdioMode` (the redirected stream has NO parent-side stream — `Spawned.Stdout`/
      // `Stderr` is `None`, exactly as `Null`/`Inherit`). Rejected at the builder boundary in combination
      // with anything that needs a parent-side view of that same stream (`StdoutTee`/`StderrTee`,
      // `OnStdoutLine`/`OnStderrLine`) and with `MergeStderr`/`Pty`; the other stream may still be captured
      // normally. Set/cleared as a pair with the matching `StdioMode` setter so the last destination wins.
      StdoutFile: (string * bool) option
      StderrFile: (string * bool) option
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
      // Merge the child's stderr into its stdout at the OS level (like a shell `2>&1`): the native spawn
      // routes the child's stderr at the SAME pipe/handle as its stdout (POSIX `dup2` of fd 2 onto
      // stdout's target; Windows shares one handle across `STARTUPINFO.hStdOutput`/`hStdError`), so the
      // two streams interleave honestly, byte for byte, on the single stdout stream. `false` (the
      // default) keeps the separate stdout/stderr behaviour unchanged. When `true` there is NO separate
      // stderr stream: `Spawned.Stderr` is `None`, `ProcessResult.Stderr` is empty, `OnStderrLine` never
      // fires, and `OutputEventsAsync` emits only `Stdout` events. Incompatible with the separate-stderr
      // observation hooks (`StderrTee`/`OnStderrLine`, rejected at the builder boundary); `StderrEncoding`/
      // `StderrLineTerminator`/`Stderr` mode become documented no-ops (the merged bytes follow stdout's
      // encoding/framing/destination). See `Command.MergeStderr`.
      MergeStderr: bool
      OutputBuffer: OutputBufferPolicy
      // Opt-in consumer-supplied transform applied to each decoded line on its way INTO the in-memory
      // capture backlog, and nowhere else — the redaction-at-capture seam (see `ICapturePolicy` for the
      // full boundary: handlers, tees, the streaming verbs and the raw byte captures all keep seeing
      // the unshaped line). `None` (the default) retains exactly what was framed, as before.
      CapturePolicy: ICapturePolicy option
      // Opt-in bounded/backpressure policy for the streaming verbs (`StdoutLinesAsync`/
      // `OutputEventsAsync`/`WaitForLineAsync`). `None` (the default) keeps the unbounded streaming
      // channels ProcessKit has always used.
      StreamBuffer: StreamBufferPolicy option
      Timeout: TimeSpan option
      TimeoutGrace: TimeSpan option
      // Soft signal used by graceful stop paths before escalation. SIGTERM preserves the historical
      // default. Windows has no general signal equivalent, so a non-default value is refused at spawn.
      StopSignal: Signal
      // Opt-in idle deadline: kill the run when neither stdout nor stderr produces output for this long
      // (each chunk of output resets it), independent of the total `Timeout`. `None` (the default) is no
      // idle deadline. A pipeline stage cannot honour it (rejected by `PipelineStageGuard`).
      IdleTimeout: TimeSpan option
      CancelOn: CancellationToken option
      // Opt-in graceful teardown for a run torn down by a fired CANCELLATION token (the verb's own,
      // `CancelOn`, `Pipeline.CancelOn`, or a `Supervisor` incarnation's): send `CancelSignal`, give the
      // tree up to this long to leave on its own, then escalate to the ordinary hard kill. `None` (the
      // default) is the historical immediate hard kill, byte for byte. Deliberately INDEPENDENT of
      // `TimeoutGrace`/`StopSignal` — "the caller changed its mind" and "the deadline expired" are
      // different events, and neither knob gap-fills the other.
      CancelGrace: TimeSpan option
      // The soft signal that opens a `CancelGrace` window. `None` (the default) means `Signal.Term`,
      // matching `StopSignal`'s default. Inert without `CancelGrace` (there is no soft tier to send it
      // on), and — exactly like `StopSignal` — a non-default value is refused at spawn on Windows, which
      // cannot represent an arbitrary POSIX signal.
      CancelSignal: Signal option
      Retry: (int * RetryDelayPolicy * Func<ProcessError, bool>) option
      // Production jitter uses Random.Shared. Tests replace this immutable seam so exponential retry
      // delays can be asserted exactly while still exercising the command's TimeProvider timers.
      RetryJitterSource: unit -> float
      // Explicit one-shot opt-out of retrying, distinct from `Retry = None` ("no policy set"). Set by
      // `RetryNever` and read by the verb layer's `withRetry`, which runs the command exactly once
      // whenever this is `true` — even if `Retry` itself carries a policy inherited from a
      // `CliClient.WithDefaults` template. `Retry`/`RetryBackoff` reset this back to `false`, so the
      // last retry-policy/`RetryNever()` call in a chain wins, like every other builder knob.
      RetryDisabled: bool
      // POSIX-only full-duplex parent/child channels. Each target fd is >= 3 and unique; the parent
      // claims its managed Stream once through RunningProcess.TakeExtraFd.
      ExtraFds: ImmutableList<int>
      UncheckedInPipe: bool
      OkCodes: int list
      CreateNoWindow: bool
      // Windows: opt the child into registration for targeted CTRL+BREAK through
      // `ProcessGroup.Signal(Signal.Int/Term)`. Regular children are also spawned in their own console
      // process group; ConPTY children already have one unconditionally for isolation. Default `false`;
      // no effect on Unix (which signals the child's process group through `killpg` regardless).
      WindowsCtrlSignals: bool
      // The child's CPU-scheduling priority, applied at spawn (Windows priority class / Unix nice).
      // `None` (the default) leaves the OS default untouched.
      Priority: Priority option
      // The child's LINUX I/O-scheduling priority (`ioprio_set(2)`) — a separate axis from the CPU
      // `Priority` above. `None` (the default) leaves the inherited I/O priority untouched,
      // byte-identical to before this knob existed. Linux-only: a set value fails a Windows, macOS, or
      // BSD spawn with `ProcessError.Unsupported`, never a silent drop, and is refused outright on the
      // detached launch (which has no owner to apply it for). On Linux it is applied by setting the
      // spawning thread's own I/O priority across the `posix_spawnp` window: the kernel copies the
      // calling task's I/O priority into the child at clone time and it survives every `execve`, so the
      // child's first block-device request already runs at the requested priority — see
      // `Native.Posix.withIoPriority`.
      IoPriority: IoPriority option
      // The child's Unix file-mode creation mask (`umask(2)`), applied at spawn on the POSIX path.
      // `None` (the default) leaves the inherited umask untouched. Unix-only: a set value fails a
      // Windows spawn with `ProcessError.Unsupported` (there is no Windows equivalent), never a silent
      // drop. Only the low permission bits are meaningful, as with `umask(2)` itself.
      Umask: int option
      // Unix per-process `setrlimit(2)` caps applied to the child before its program starts, in the
      // order the builder added them, at most one entry per resource (`Command.Rlimit` replaces a
      // repeated resource in place). Empty (the default) applies none, byte-identical to before this
      // knob existed. Unix-only: a non-empty set fails a Windows spawn with `ProcessError.Unsupported`,
      // never a silent drop. On POSIX the whole set is handed to the util-linux `prlimit` helper, which
      // applies it to itself and then `exec`s the real program IN PLACE (same pid, so containment,
      // priority, and any pty the rest of the chain set up are unchanged) — see
      // `Native.Posix.withProcessLimits`.
      Rlimits: ImmutableList<Rlimit>
      // Unix privilege drop: run the child under this user id (`setuid`) / group id (`setgid`). `None`
      // (the default) inherits the parent's ids. Unix-only: a set value fails a Windows spawn with
      // `ProcessError.Unsupported`, never a silent drop. Because `posix_spawn` exposes no uid/gid
      // attribute (and forking a managed runtime to drop in a child is unsafe on .NET), a spawn that
      // requests either is rewritten to run through the `setpriv` helper (util-linux) on the ordinary
      // `posix_spawn` path (see `Native.Posix.setprivCommand`): `setpriv` sets the gid before the uid and
      // clears the parent's supplementary groups, then `exec`s the real program. A non-root caller asking
      // for a different id is rejected up front with `ProcessError.Spawn`; so is a `setpriv` that no
      // trusted system directory (`/usr/bin`, `/bin`, `/usr/sbin`, `/sbin`) holds — the helper is never
      // resolved on `PATH`, so being reachable there is not enough (see `Native.Posix.trustedHelperPath`).
      Uid: int option
      Gid: int option
      // Unix privilege drop: the child's supplementary groups, REPLACING the inherited set — the third
      // leg of a correct drop, next to `Uid`/`Gid`. `None` (the default) keeps the `setpriv` path's
      // `--clear-groups` behaviour (a uid/gid drop clears the parent's supplementary groups so the child
      // never keeps root's). `Some gids` sets EXACTLY those groups via `setpriv --groups` (an explicit
      // `Some []` clears them, identical on the wire to the `None` default). Because it rides the same
      // `setpriv` helper as the uid/gid drop, it takes effect only alongside one: a `Groups` request
      // without `Uid`/`Gid` is refused up front with `ProcessError.Spawn` (never a silent no-op), and on
      // Windows any set value fails the spawn with `ProcessError.Unsupported`, exactly like `Uid`/`Gid`.
      Groups: int list option
      // Windows privilege reduction: spawn the child with a RESTRICTED copy of this process's own primary
      // token (`CreateRestrictedToken` with `DISABLE_MAX_PRIVILEGE`, then `CreateProcessAsUser`), so the
      // child holds no privilege beyond the always-present `SeChangeNotifyPrivilege`. `false` (the
      // default) spawns through the ordinary `CreateProcessW` with the inherited token, byte-identical to
      // before this option existed. Windows-only: a set value fails a POSIX spawn with
      // `ProcessError.Unsupported`, never a silent no-op — the mirror image of how `Uid`/`Gid`/`Setsid`
      // fail on Windows. Rejected at the builder boundary alongside `Pty` (the ConPTY path has its own
      // spawn call, which is not on this token path) and alongside the Unix drop family (a command
      // carrying both could not run on ANY platform).
      WindowsRestrictedToken: bool
      // Windows integrity drop: lower the child's token to this mandatory integrity level
      // (`SetTokenInformation(TokenIntegrityLevel, ...)` on a duplicated primary token, spawned with
      // `CreateProcessAsUser`). `None` (the default) inherits the parent's level. Independent of
      // `WindowsRestrictedToken` — integrity governs what the child may WRITE to, privileges what it may
      // DO — and the two compose on one token when both are set. Windows-only and rejected in the same
      // combinations, with the same honest `ProcessError.Unsupported` on POSIX.
      WindowsIntegrityLevel: WindowsIntegrityLevel option
      // Unix `setsid()`: detach the child into a brand-new session with no controlling terminal. `false`
      // (the default) leaves the child in the caller's session. Unix-only: `true` fails a Windows spawn
      // with `ProcessError.Unsupported`. `setsid()` also makes the child a new process-group leader
      // (pgid == pid == sid), so it REPLACES the group's `POSIX_SPAWN_SETPGROUP` for that command rather
      // than combining with it; the kill-on-drop `killpg(pid)` teardown still reaches the whole session,
      // so containment is preserved (see `Native.Posix`).
      Setsid: bool
      // Unix override of the child's `argv[0]` independently of `Program` (see `Command.Arg0`).
      // `None` (the default) is the ordinary behaviour: `Program` supplies both the executable
      // lookup AND `argv[0]`, byte-identical to before this knob existed. Applied only on the two
      // POSIX paths that spawn the target directly (`spawnPosixViaSpawn`/`spawnDetachedPosixCore`
      // in `Native.Posix`); `Program` itself is UNCHANGED and still drives PATH resolution,
      // `PreferLocal`, and every diagnostic (`ProcessError`, logging, cassette matching). A helper
      // that must re-`exec` the target by name — `setpriv` (`Uid`/`Gid`/`Groups`/
      // `KillOnParentDeath`), the `setsid --ctty` pty shim, the cgroup migration launcher, or the
      // `CpuTimeMax` `RLIMIT_CPU` shim — has no CLI seam of its own to carry a distinct `argv[0]`,
      // so combining `Arg0` with any of them is refused with a typed `ProcessError.Unsupported`
      // rather than silently applying to the WRONG process (the helper's own `argv[0]`) or being
      // dropped. Windows has no separate `argv[0]` contract, so a set value there fails the spawn
      // with `ProcessError.Unsupported` too, never a silent fallback to `Program`.
      Arg0: string option
      // Opt-in reaping of the child if the PARENT process dies SUDDENLY (SIGKILL / crash /
      // `TerminateProcess`) — a case the deterministic `Dispose`/`DisposeAsync` kill-on-drop cannot
      // cover because no managed teardown runs. `false` (the default) leaves the behaviour unchanged.
      // Linux: armed as `PR_SET_PDEATHSIG(SIGKILL)` through the `setpriv --pdeathsig` helper on the
      // ordinary `posix_spawn` path, with a `/bin/sh` guard right behind it for a parent that died
      // before the arming (see `Native.Posix`) — reaches the direct child only, and is reset
      // by the kernel across an `execve` of a set-uid/set-gid image. Windows: no extra action — every
      // child already lives in a Job Object with `KILL_ON_JOB_CLOSE`, whose sole handle the parent owns,
      // so the kernel's handle rundown on parent death terminates the whole Job tree. macOS/BSD: a set
      // value fails the spawn with `ProcessError.Unsupported` (no `pdeathsig` analog), never a silent
      // no-op. The platform-fixed *scope* of this cleanup is reported by `Command.KillOnParentDeathScope`.
      KillOnParentDeath: bool
      // Opt-in pseudo-terminal (PTY) mode: run the child under a real controlling terminal on a single
      // merged stdout+stderr stream, instead of the default parent/child pipes. `None` (the default) is
      // the plain pipe run, byte-identical to before PTY existed. A PTY implies OS-level merge semantics
      // (there is one terminal stream — `Spawned.Stderr` is `None`, `OutputEvent.Stderr` is never
      // produced), so it is rejected at the builder boundary alongside the separate-stderr observation
      // hooks (`StderrTee`/`OnStderrLine`) and alongside `Setsid` (a new session with NO controlling
      // tty, contradicting a PTY's controlling tty), and only as a standalone run or the last stage of a
      // pipeline. Windows: ConPTY (`CreatePseudoConsole`, Windows 10 1809+; older hosts fail the spawn
      // with a typed `ProcessError.Unsupported`, never a silent pipe downgrade). POSIX: `openpty` +
      // `setsid --ctty` (util-linux, loaded only from a trusted system directory and never from `PATH`,
      // as for the `setpriv` helper above); a host with the ctty helper in no trusted directory, or
      // without the pty devfs (macOS/BSD), is a typed `ProcessError.Unsupported`, never a socketpair
      // pretending to be a tty. See `Command.Pty`.
      Pty: PtyConfig option
      Logger: ILogger option
      // A per-run correlation id, stamped once at the verb layer so a run's log/trace events (and its
      // retries) share it. `None` until stamped; a direct spawn gets a per-incarnation id instead.
      RunId: string option }

module internal CommandConfig =

    let create (program: string) =
        { Program = program
          Args = ImmutableList<string>.Empty
          WindowsRawArgs = ImmutableList<string>.Empty
          WorkingDirectory = None
          PreferLocal = ImmutableList<string>.Empty
          EnvOverrides = ImmutableList<string * string option>.Empty
          ClearEnv = false
          StdinSource = None
          StdinReservation = None
          KeepStdinOpen = false
          StdinEncoding = Encoding.UTF8
          TimeProvider = TimeProvider.System
          StdoutMode = StdioMode.Piped
          StderrMode = StdioMode.Piped
          StdoutFile = None
          StderrFile = None
          StdoutEncoding = Encoding.UTF8
          StderrEncoding = Encoding.UTF8
          StdoutLineTerminator = LineTerminator.Lf
          StderrLineTerminator = LineTerminator.Lf
          OnStdoutLine = None
          OnStderrLine = None
          StdoutTee = None
          StderrTee = None
          MergeStderr = false
          OutputBuffer = OutputBufferPolicy.Default
          CapturePolicy = None
          StreamBuffer = None
          Timeout = None
          TimeoutGrace = None
          StopSignal = Signal.Term
          IdleTimeout = None
          CancelOn = None
          CancelGrace = None
          CancelSignal = None
          Retry = None
          RetryJitterSource = fun () -> Random.Shared.NextDouble()
          RetryDisabled = false
          ExtraFds = ImmutableList<int>.Empty
          UncheckedInPipe = false
          OkCodes = [ 0 ]
          CreateNoWindow = false
          WindowsCtrlSignals = false
          Priority = None
          IoPriority = None
          Umask = None
          Rlimits = ImmutableList<Rlimit>.Empty
          Uid = None
          Gid = None
          Groups = None
          WindowsRestrictedToken = false
          WindowsIntegrityLevel = None
          Setsid = false
          Arg0 = None
          KillOnParentDeath = false
          Pty = None
          Logger = None
          RunId = None }

    /// The soft signal a graceful CANCELLATION teardown opens with: `Command.CancelSignal` when set,
    /// otherwise `Signal.Term` — the same default `StopSignal` carries. One resolver, so every path that
    /// drives the cancellation ladder (the completion verbs, a pipeline chain, a supervised incarnation)
    /// sends the same signal and the Windows spawn refusal can screen exactly the value that would be
    /// sent. Deliberately does NOT fall back to `StopSignal`: the two knobs are independent by contract.
    let cancelSignal (config: CommandConfig) =
        config.CancelSignal |> Option.defaultValue Signal.Term

    /// Reject a string carrying an embedded NUL (`'\000'`) at the `Command`/`CommandConfig` builder
    /// boundary. POSIX argv/environment marshalling treats a NUL byte as the end of a string (or, for
    /// a raw pointer, the end of the whole array), and the Windows command-line / environment-block
    /// encodings truncate at the first embedded NUL — so a string that reached the native layer with
    /// one inside could silently run (or observe) something other than what was actually requested.
    /// Checked once, here, before dispatch to either backend (`Native.Posix`/`Native.Windows`), so the
    /// rejection is identical regardless of which one ends up spawning. `paramName` names the actual
    /// offending public parameter/element (`program`, `Args[2]`, `cwd`, an env key or value, …) so the
    /// exception points straight at the culprit.
    let rejectEmbeddedNul (paramName: string) (value: string) =
        if value.Contains '\000' then
            raise (ArgumentException($"{paramName} must not contain an embedded NUL character ('\\0')", paramName))

    /// Validate an environment-variable key for `Command.Env`/`Command.EnvRemove`: must be non-empty,
    /// must not contain `=` (an env var name can never contain one; a key that did would corrupt the
    /// child's environment block, since `KEY=VALUE` is the wire format on every platform), and must not
    /// contain an embedded NUL (see `rejectEmbeddedNul`).
    let validateEnvKey (key: string) =
        if key.Length = 0 then
            raise (ArgumentException("an environment variable key must not be empty", nameof key))
        elif key.Contains '=' then
            raise (ArgumentException("an environment variable key must not contain '='", nameof key))
        else
            rejectEmbeddedNul (nameof key) key

    /// Validate an environment-variable value for `Command.Env`: must not contain an embedded NUL (see
    /// `rejectEmbeddedNul`). Unlike the key, an env value has no other shape restriction.
    let validateEnvValue (value: string) = rejectEmbeddedNul (nameof value) value

    /// The `ArgumentException` for combining `MergeStderr` with a separate-stderr observation hook. Named
    /// after the offending knob so the message points at whichever of the pair was set second (the check
    /// is bidirectional — see below), the project's "no silent downgrade" rule at the builder boundary.
    let private mergeStderrConflict (knob: string) =
        ArgumentException(
            $"{knob} cannot be combined with MergeStderr: MergeStderr folds the child's stderr into its stdout at the OS level (like a shell 2>&1), so there is no separate stderr stream for {knob} to observe. Drop one of the two.",
            knob
        )

    /// Guard `MergeStderr()`: reject it when a separate-stderr observation hook (`StderrTee`/`OnStderrLine`)
    /// is already set. `StderrEncoding`/`StderrLineTerminator`/`Stderr` mode are deliberately NOT rejected
    /// (documented no-ops under merge, and `Encoding()`/`LineTerminator()` set them as a pair, so rejecting
    /// them would make those pair setters conflict with `MergeStderr`).
    let ensureNoMergeStderrObservers (config: CommandConfig) =
        if config.StderrTee.IsSome then
            raise (mergeStderrConflict "StderrTee")

        if config.OnStderrLine.IsSome then
            raise (mergeStderrConflict "OnStderrLine")

    /// Guard `StderrTee`/`OnStderrLine`: reject them when `MergeStderr` is already set. The mirror of
    /// `ensureNoMergeStderrObservers`, so the conflict is caught regardless of the order the two knobs are
    /// chained in.
    let ensureNoMergeStderr (config: CommandConfig) (knob: string) =
        if config.MergeStderr then
            raise (mergeStderrConflict knob)

    // Reasons a knob cannot coexist with `Pty`, reused so the message is identical regardless of which of
    // the pair was set second (the checks below are bidirectional, mirroring `mergeStderrConflict`).
    [<Literal>]
    let private ptyMergedStreamReason =
        "a PTY gives the child a single merged terminal stream (its stdout and stderr are one device), so there is no separate stderr stream to observe"

    [<Literal>]
    let private ptySetsidReason =
        "Setsid detaches the child into a new session with NO controlling terminal, whereas Pty gives it a new session WITH a controlling pseudo-terminal — the two are contradictory"

    [<Literal>]
    let private ptyInheritStdinReason =
        "a PTY replaces the child's stdin with the pty slave/ConPTY input pipe, so there is no way to also hand it the parent's own standard input — InheritStdin would be silently ignored"

    [<Literal>]
    let private ptyWindowsTokenReason =
        "a PTY run is created through the separate ConPTY spawn path (a pseudoconsole attribute on CreateProcessExtended), which does not take the hardened token — the child would keep the parent's full token while the call looked like it had been honoured"

    /// The `ArgumentException` for combining `Pty` with a knob a pseudo-terminal cannot honour. Named after
    /// the offending knob so the message points at whichever of the pair was set second, per the project's
    /// "reject conflicts at the builder boundary, never a silent downgrade" rule.
    let private ptyConflict (knob: string) (reason: string) =
        ArgumentException($"{knob} cannot be combined with Pty: {reason}. Drop one of the two.", knob)

    /// Guard `Pty(...)`: reject it when a separate-stderr observation hook (`StderrTee`/`OnStderrLine`, D4),
    /// `Setsid` (D8), `InheritStdin`, or a Windows token-hardening knob is already set. A PTY implies
    /// OS-level merge semantics (one terminal stream, no separate stderr), a controlling pseudo-terminal
    /// (contradicting `Setsid`'s controlling-tty-less new session), its own stdin device (contradicting
    /// `InheritStdin`'s promise of the parent's own standard input), and its own ConPTY spawn call
    /// (which does not carry the hardened token).
    let ensurePtyCompatible (config: CommandConfig) =
        if config.StderrTee.IsSome then
            raise (ptyConflict "StderrTee" ptyMergedStreamReason)

        if config.OnStderrLine.IsSome then
            raise (ptyConflict "OnStderrLine" ptyMergedStreamReason)

        if config.Setsid then
            raise (ptyConflict "Setsid" ptySetsidReason)

        if Stdin.isInherit config.StdinSource then
            raise (ptyConflict "InheritStdin" ptyInheritStdinReason)

        if config.WindowsRestrictedToken then
            raise (ptyConflict "WindowsRestrictedToken" ptyWindowsTokenReason)

        if config.WindowsIntegrityLevel.IsSome then
            raise (ptyConflict "WindowsIntegrityLevel" ptyWindowsTokenReason)

    /// Guard `StderrTee`/`OnStderrLine`: reject them when `Pty` is already set — the mirror of
    /// `ensurePtyCompatible`'s observer checks, so the conflict is caught in either chaining order.
    let ensureNoPty (config: CommandConfig) (knob: string) =
        if config.Pty.IsSome then
            raise (ptyConflict knob ptyMergedStreamReason)

    /// Guard `Setsid()`: reject it when `Pty` is already set — the mirror of `ensurePtyCompatible`'s
    /// `Setsid` check (D8), so the conflict is caught in either chaining order.
    let ensureNoPtyForSetsid (config: CommandConfig) =
        if config.Pty.IsSome then
            raise (ptyConflict "Setsid" ptySetsidReason)

    /// The Unix-only privilege/session knob currently set on `config`, if any. Feeds the bidirectional
    /// guard below; the order is only which one gets named first when several are set.
    let private unixPrivilegeKnob (config: CommandConfig) : string option =
        if config.Uid.IsSome then Some "Uid"
        elif config.Gid.IsSome then Some "Gid"
        elif config.Groups.IsSome then Some "Groups"
        elif config.Umask.IsSome then Some "Umask"
        elif config.Setsid then Some "Setsid"
        else None

    /// The `ArgumentException` for combining a Windows-only token-hardening knob
    /// (`WindowsRestrictedToken`/`WindowsIntegrityLevel`) with a Unix-only privilege/session knob
    /// (`Uid`/`Gid`/`Groups`/`Umask`/`Setsid`). Unlike the other conflicts here this is not a
    /// contradiction *within* one platform: it is a command no platform can run, because each half fails
    /// the spawn with `ProcessError.Unsupported` on exactly the platform the other half needs. Caught at
    /// the builder boundary — where the mistake is — instead of being left to surface as a runtime
    /// refusal on whichever host the caller happens to be on. `named` is whichever knob was set second,
    /// so the exception points at the call that introduced the conflict.
    let private crossPlatformHardeningConflict (named: string) (windowsKnob: string) (unixKnob: string) =
        ArgumentException(
            $"{windowsKnob} (Windows-only) cannot be combined with {unixKnob} (Unix-only): each fails the spawn with ProcessError.Unsupported on the platform the other one needs, so the command could not run on any host. Drop one of the two.",
            named
        )

    /// Guard `WindowsRestrictedToken()`/`WindowsIntegrityLevel(...)`: reject them when `Pty` (which spawns
    /// through the ConPTY path, not the token path) or a Unix-only privilege/session knob is already set.
    let ensureWindowsTokenHardeningCompatible (config: CommandConfig) (knob: string) =
        if config.Pty.IsSome then
            raise (ptyConflict knob ptyWindowsTokenReason)

        match unixPrivilegeKnob config with
        | Some unixKnob -> raise (crossPlatformHardeningConflict knob knob unixKnob)
        | None -> ()

    /// Guard the Unix-only privilege/session knobs (`Uid`/`Gid`/`User`/`Groups`/`Umask`/`Setsid`): reject
    /// them when a Windows-only token-hardening knob is already set — the mirror of
    /// `ensureWindowsTokenHardeningCompatible`, so the conflict is caught in either chaining order.
    let ensureNoWindowsTokenHardening (config: CommandConfig) (knob: string) =
        if config.WindowsRestrictedToken then
            raise (crossPlatformHardeningConflict knob "WindowsRestrictedToken" knob)

        if config.WindowsIntegrityLevel.IsSome then
            raise (crossPlatformHardeningConflict knob "WindowsIntegrityLevel" knob)

    // The `ArgumentException` for combining a stdout/stderr file redirect (`StdoutToFile`/`StderrToFile`)
    // with a knob that needs a parent-side view of that same stream, or with `MergeStderr`/`Pty`. Named
    // after the OTHER knob (the check is bidirectional, mirroring `mergeStderrConflict`) so the message is
    // identical regardless of which of the pair was set second. A file-redirected stream is handed to the
    // child straight at the OS level, so there is no separate parent-side stream at all.
    let private stdoutFileConflict (knob: string) =
        ArgumentException(
            $"{knob} cannot be combined with StdoutToFile: StdoutToFile hands the child's stdout straight to a file at the OS level (the file grows even after the parent's pump is gone), so there is no separate parent-side stdout stream. Drop one of the two.",
            knob
        )

    let private stderrFileConflict (knob: string) =
        ArgumentException(
            $"{knob} cannot be combined with StderrToFile: StderrToFile hands the child's stderr straight to a file at the OS level (the file grows even after the parent's pump is gone), so there is no separate parent-side stderr stream. Drop one of the two.",
            knob
        )

    /// Guard `StdoutToFile(...)`: reject it when a knob that needs a parent-side stdout stream
    /// (`StdoutTee`/`OnStdoutLine`) — or `MergeStderr` (which folds stderr into the stdout stream the
    /// parent observes) or `Pty` (which replaces the child's stdio with one terminal device) — is already
    /// set. The stderr side is deliberately NOT checked here: redirecting stdout to a file while capturing
    /// stderr normally (and vice versa) is an explicitly supported combination.
    let ensureStdoutFileCompatible (config: CommandConfig) =
        if config.StdoutTee.IsSome then
            raise (stdoutFileConflict "StdoutTee")

        if config.OnStdoutLine.IsSome then
            raise (stdoutFileConflict "OnStdoutLine")

        if config.MergeStderr then
            raise (stdoutFileConflict "MergeStderr")

        if config.Pty.IsSome then
            raise (stdoutFileConflict "Pty")

    /// Guard `StderrToFile(...)`: the stderr mirror of `ensureStdoutFileCompatible`.
    let ensureStderrFileCompatible (config: CommandConfig) =
        if config.StderrTee.IsSome then
            raise (stderrFileConflict "StderrTee")

        if config.OnStderrLine.IsSome then
            raise (stderrFileConflict "OnStderrLine")

        if config.MergeStderr then
            raise (stderrFileConflict "MergeStderr")

        if config.Pty.IsSome then
            raise (stderrFileConflict "Pty")

    /// Guard `StdoutTee`/`OnStdoutLine`: reject them when stdout is already redirected to a file — the
    /// mirror of `ensureStdoutFileCompatible`, so the conflict is caught in either chaining order.
    let ensureNoStdoutFile (config: CommandConfig) (knob: string) =
        if config.StdoutFile.IsSome then
            raise (stdoutFileConflict knob)

    /// Guard `StderrTee`/`OnStderrLine`: the stderr mirror of `ensureNoStdoutFile`.
    let ensureNoStderrFile (config: CommandConfig) (knob: string) =
        if config.StderrFile.IsSome then
            raise (stderrFileConflict knob)

    let private stdoutModeConflict (knob: string) =
        let purpose =
            if knob = "OnStdoutLine" then
                "observe per-line updates"
            else
                "receive and copy captured bytes"

        ArgumentException(
            $"{knob} cannot be combined with Stdout(Null) or Stdout(Inherit): when stdout is Null or Inherit, there is no separate parent-side stream for {knob} to {purpose}. Drop one of the two.",
            knob
        )

    let private stderrModeConflict (knob: string) =
        let purpose =
            if knob = "OnStderrLine" then
                "observe per-line updates"
            else
                "receive and copy captured bytes"

        ArgumentException(
            $"{knob} cannot be combined with Stderr(Null) or Stderr(Inherit): when stderr is Null or Inherit, there is no separate parent-side stream for {knob} to {purpose}. Drop one of the two.",
            knob
        )

    /// Guard `Stdout(Null|Inherit)`: reject it when a parent-side stdout observer is already set.
    /// `MergeStderr` is deliberately not checked; it remains valid with every stdout destination.
    let ensureStdoutModeCompatible (config: CommandConfig) (mode: StdioMode) =
        if mode <> StdioMode.Piped then
            if config.StdoutTee.IsSome then
                raise (stdoutModeConflict "StdoutTee")

            if config.OnStdoutLine.IsSome then
                raise (stdoutModeConflict "OnStdoutLine")

    /// Guard `Stderr(Null|Inherit)`: the stderr mirror of `ensureStdoutModeCompatible`.
    let ensureStderrModeCompatible (config: CommandConfig) (mode: StdioMode) =
        if mode <> StdioMode.Piped then
            if config.StderrTee.IsSome then
                raise (stderrModeConflict "StderrTee")

            if config.OnStderrLine.IsSome then
                raise (stderrModeConflict "OnStderrLine")

    /// Guard `StdoutTee`/`OnStdoutLine`: reject them when stdout has no parent-side pipe.
    let ensureStdoutPiped (config: CommandConfig) (knob: string) =
        if config.StdoutMode <> StdioMode.Piped then
            raise (stdoutModeConflict knob)

    /// Guard `StderrTee`/`OnStderrLine`: reject them when stderr has no parent-side pipe.
    let ensureStderrPiped (config: CommandConfig) (knob: string) =
        if config.StderrMode <> StdioMode.Piped then
            raise (stderrModeConflict knob)

    /// Guard `MergeStderr()`/`Pty(...)`: reject them when EITHER stream is already redirected to a file.
    /// `MergeStderr` needs a parent-observable stdout stream to fold stderr into (absent when stdout is a
    /// file) and no separate stderr stream (contradicted when stderr is a file); a `Pty` replaces all of
    /// the child's stdio with one terminal device, leaving nothing to redirect. The mirror of the
    /// `MergeStderr`/`Pty` checks in `ensureStdoutFileCompatible`/`ensureStderrFileCompatible`, so the
    /// conflict is caught in either chaining order.
    let ensureNoFileRedirect (config: CommandConfig) (knob: string) =
        if config.StdoutFile.IsSome then
            raise (stdoutFileConflict knob)

        if config.StderrFile.IsSome then
            raise (stderrFileConflict knob)

    // Idle activity is observed by the parent-side output pumps. A PTY always exposes its single
    // merged master stream; otherwise only an effective Piped destination is observable (stderr follows
    // stdout under MergeStderr, and a direct file redirect takes precedence over the matching mode).
    let private hasParentSideOutput (config: CommandConfig) =
        config.Pty.IsSome
        || (config.StdoutFile.IsNone && config.StdoutMode = StdioMode.Piped)
        || (not config.MergeStderr
            && config.StderrFile.IsNone
            && config.StderrMode = StdioMode.Piped)

    let private idleTimeoutConflict () =
        ArgumentException(
            "IdleTimeout requires at least one parent-side output stream whose reads can reset the idle deadline. Keep stdout or stderr Piped, or use Pty; Null, Inherit, and direct file redirects cannot report output activity to the parent.",
            "IdleTimeout"
        )

    /// Reject an armed `IdleTimeout` once a prospective builder state has no output stream the parent
    /// can observe. Every relevant destination setter calls this on the state it is about to return, so
    /// the conflict is caught whichever setting appears last in a fluent chain.
    let ensureIdleTimeoutCompatible (config: CommandConfig) =
        if config.IdleTimeout.IsSome && not (hasParentSideOutput config) then
            raise (idleTimeoutConflict ())

    /// Validate a `PtyConfig`'s geometry at the `Command.Pty` builder boundary: both dimensions must be at
    /// least 1 (a terminal has no zero/negative size) and fit a Win32 `COORD`'s `SHORT`
    /// (`Int16.MaxValue`, also a sane ceiling on POSIX `winsize`), rejected with
    /// `ArgumentOutOfRangeException` naming the offending dimension.
    let validatePtyConfig (pty: PtyConfig) =
        ArgumentOutOfRangeException.ThrowIfLessThan(pty.Cols, 1, "Cols")
        ArgumentOutOfRangeException.ThrowIfGreaterThan(pty.Cols, int Int16.MaxValue, "Cols")
        ArgumentOutOfRangeException.ThrowIfLessThan(pty.Rows, 1, "Rows")
        ArgumentOutOfRangeException.ThrowIfGreaterThan(pty.Rows, int Int16.MaxValue, "Rows")

    /// The `ArgumentException` for combining `InheritStdin` with an incompatible stdin knob. `InheritStdin`
    /// hands the child the parent's own standard input directly, with no pipe — so there is nothing for a
    /// feeder source, `KeepStdinOpen`, or the interactive `TakeStdin` to attach to. Named after the
    /// offending knob (the check is bidirectional, mirroring `mergeStderrConflict`), the project's "no
    /// silent downgrade / reject conflicts at the builder boundary" rule.
    let private stdinInheritConflict (knob: string) =
        ArgumentException(
            $"{knob} cannot be combined with InheritStdin: InheritStdin hands the child the parent's own standard input directly, with no stdin pipe for a feeder source, KeepStdinOpen, or interactive TakeStdin writing to attach to. Drop one of the two.",
            knob
        )

    /// Guard `Stdin`/`KeepStdinOpen`: reject them when `InheritStdin` is already set. There is no stdin
    /// pipe under inherit for a feeder source or a kept-open interactive stream to use.
    let ensureNoStdinInherit (config: CommandConfig) (knob: string) =
        match config.StdinSource with
        | Some source when Stdin.isInherit (Some source) -> raise (stdinInheritConflict knob)
        | _ -> ()

    /// Guard `InheritStdin`: reject it when a feeder source (`Stdin`), `KeepStdinOpen`, or `Pty` is already
    /// set. The first two mirror `ensureNoStdinInherit`, so the conflict is caught in either chaining order;
    /// `Pty` is rejected because a pseudo-terminal replaces the child's stdin with its own device (the mirror
    /// of `ensurePtyCompatible`'s `InheritStdin` check).
    let ensureInheritStdinCompatible (config: CommandConfig) =
        if config.KeepStdinOpen then
            raise (stdinInheritConflict "KeepStdinOpen")

        match config.StdinSource with
        | Some source when not (Stdin.isInherit (Some source)) -> raise (stdinInheritConflict "Stdin")
        | _ -> ()

        if config.Pty.IsSome then
            raise (ptyConflict "InheritStdin" ptyInheritStdinReason)

/// An immutable description of a process to run.
///
/// Build it fluently — each method returns a new `Command`. The value is the *cold* description
/// of a run; the process is launched only when a verb (`Runner.run`, `Command.start`, …) is
/// invoked. Use the instance methods (`cmd.Arg "x"`) or the `Command` module's pipe-friendly
/// functions (`cmd |> Command.arg "x"`).
[<Sealed>]
type Command internal (config: CommandConfig) =

    /// Start a new command for the given program (resolved on PATH unless a path is given). `program`
    /// must be non-empty and must not contain an embedded NUL (`'\000'`) — either would let the actual
    /// spawned command diverge from the one requested (see `CommandConfig.rejectEmbeddedNul`).
    new(program: string) =
        ArgumentNullException.ThrowIfNull program

        if program.Length = 0 then
            raise (ArgumentException("program must not be empty", nameof program))

        CommandConfig.rejectEmbeddedNul (nameof program) program
        Command(CommandConfig.create program)

    member internal _.Config = config

    /// The program to run.
    member _.Program = config.Program

    /// The ordinary arguments followed by any Windows raw fragments, in their respective append order.
    /// Raw fragments are opaque values for test doubles and cassette matching; they are not portable argv
    /// elements and a real POSIX spawn rejects them.
    member _.Arguments: IReadOnlyList<string> =
        Seq.append config.Args config.WindowsRawArgs |> Seq.toArray :> IReadOnlyList<string>

    /// The working directory, when overridden.
    member _.WorkingDirectory = config.WorkingDirectory

    /// The per-process Unix rlimits configured through `Command.Rlimit`, in the order they were added
    /// (at most one entry per resource), or an empty list when none were — so a config-driven caller can
    /// read back exactly what it asked for, and a diagnostic can report it. A fresh list each read, so a
    /// caller can never mutate a command's limits through it.
    member _.Rlimits: IReadOnlyList<Rlimit> =
        config.Rlimits |> Seq.toArray :> IReadOnlyList<Rlimit>

    /// Append a single argument. `value` must not contain an embedded NUL (`'\000'`) — see
    /// `CommandConfig.rejectEmbeddedNul`.
    member _.Arg(value: string) =
        ArgumentNullException.ThrowIfNull value
        CommandConfig.rejectEmbeddedNul (nameof value) value

        Command(
            { config with
                Args = config.Args.Add value }
        )

    /// Append several arguments, in order. Every element must be non-null (a null element inside an
    /// otherwise non-null `seq` — a C#-reachable shape `ArgumentNullException.ThrowIfNull values` on
    /// the sequence itself cannot catch) and must not contain an embedded NUL (`'\000'`); the exception
    /// names the offending element by index (`Args[2]`).
    member _.Args(values: seq<string>) =
        ArgumentNullException.ThrowIfNull values

        let materialized = values |> Seq.toArray

        materialized
        |> Array.iteri (fun index value ->
            let paramName = $"Args[{index}]"

            if isNull (box value) then
                raise (ArgumentNullException(paramName, "an Args element must not be null"))

            CommandConfig.rejectEmbeddedNul paramName value)

        Command(
            { config with
                Args = config.Args.AddRange materialized }
        )

    /// Append a Windows command-line fragment verbatim after all ordinarily quoted arguments. Raw
    /// fragments retain their own insertion order, but ordinary `Arg`/`Args` values always precede them,
    /// regardless of builder-call order. This is an explicit escape hatch for programs with non-MSVCRT
    /// parsers; never place untrusted input in `fragment`. POSIX spawn returns typed `Unsupported`, and an
    /// automatically resolved `.cmd`/`.bat` target is refused (invoke `cmd.exe` explicitly when needed).
    member _.WindowsRawArg(fragment: string) =
        ArgumentNullException.ThrowIfNull fragment
        CommandConfig.rejectEmbeddedNul (nameof fragment) fragment

        Command(
            { config with
                WindowsRawArgs = config.WindowsRawArgs.Add fragment }
        )

    /// Set the working directory for the run. `directory` must not contain an embedded NUL (`'\000'`)
    /// — see `CommandConfig.rejectEmbeddedNul`.
    member _.CurrentDir(directory: string) =
        ArgumentNullException.ThrowIfNull directory
        CommandConfig.rejectEmbeddedNul (nameof directory) directory

        Command(
            { config with
                WorkingDirectory = Some directory }
        )

    /// Add `directory` to a priority search list consulted **before** `PATH` when resolving this
    /// command's **bare-name** program: the prefer-local directories are searched first, in the order
    /// they were added, and only then the inherited `PATH`. The canonical use is preferring a
    /// project-local tool — `node_modules/.bin`, `.venv/bin`, `tools/`, a binary next to the solution —
    /// over a global one of the same name, without hand-building the path and losing cross-platform
    /// executable resolution.
    ///
    /// The lookup in each directory is the SAME `PATHEXT`-aware (Windows) / executable-bit (POSIX) probe
    /// the `PATH` walk itself uses, so a Windows `.cmd`/`.bat` shim resolves — and launches through
    /// `cmd.exe /d /c` — exactly as it would on `PATH`, and a POSIX file without an executable bit is
    /// skipped just the same. A **relative** `directory` resolves against this command's `CurrentDir`
    /// when one is set (so a project-relative `tools/` anchors to where the child will actually run, not
    /// the parent's current directory); otherwise it resolves against the process's current directory. A
    /// prefer-local match is ALWAYS handed to the OS as its resolved **absolute** path, whatever its
    /// extension — the OS never searches these directories on its own. Only a **bare name** is affected:
    /// a path-form program (`./tool`, `/usr/bin/tool`, `C:\tools\tool.exe`) is launched directly and
    /// ignores prefer-local, exactly as it ignores `PATH`. `Exec.which` is deliberately unchanged — it
    /// answers "is this installed on the host", a preflight question, whereas prefer-local is a launch
    /// concern. `directory` must not contain an embedded NUL (`'\000'`). Repeatable: each call appends
    /// one more directory to the end of the priority list.
    member _.PreferLocal(directory: string) =
        ArgumentNullException.ThrowIfNull directory
        CommandConfig.rejectEmbeddedNul (nameof directory) directory

        Command(
            { config with
                PreferLocal = config.PreferLocal.Add directory }
        )

    /// Set an environment variable for the child. `key` must be non-empty, must not contain `=`, and
    /// neither `key` nor `value` may contain an embedded NUL (`'\000'`) — all rejected with
    /// `ArgumentException` (either would corrupt the child's environment block, or let it diverge from
    /// what was requested).
    member _.Env(key: string, value: string) =
        ArgumentNullException.ThrowIfNull key
        ArgumentNullException.ThrowIfNull value
        CommandConfig.validateEnvKey key
        CommandConfig.validateEnvValue value

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

    /// Feed the child's standard input from `source`. Rejected (`ArgumentException`) when `InheritStdin`
    /// is already set — the inherited stdin has no pipe for a feeder source to write into.
    ///
    /// A **one-shot** source (`Stdin.FromStream`/`FromLines`/`FromAsyncLines`) feeds at most ONE
    /// incarnation: the launch that creates a child takes it before spawning, so a second consumer —
    /// a later run, a concurrent one, another verb or runner — is refused with
    /// `ProcessError.Unsupported` before any child of its own exists, rather than being handed the
    /// exhausted remains. That holds whatever drives the command and whether or not it carries a
    /// `Retry` policy: a decorator that calls its inner runner twice with the same command, or a
    /// command a runner kept and started afterwards, is a second consumer like any other.
    /// A launch that produced no child leaves the source intact for the next one.
    /// The repeatable sources (`Stdin.FromString`/`FromBytes`/`FromFile`/`Stdin.Empty`) feed every run.
    member _.Stdin(source: Stdin) =
        ArgumentNullException.ThrowIfNull source
        CommandConfig.ensureNoStdinInherit config "Stdin"

        Command(
            { config with
                StdinSource = Some source }
        )

    /// Hand the child the parent process's **own standard input** directly — inherited, with no pipe and
    /// no feeder — for interactive/console programs that read from the terminal (an editor launched by
    /// `git commit`, a tool that prompts the user, a pipe from the parent's own stdin). This is the stdin
    /// analogue of `StdioMode.Inherit` for stdout/stderr. Because there is no stdin pipe, it is
    /// incompatible with the pipe-based stdin knobs and rejected together with them at the builder
    /// boundary (`ArgumentException`, in either chaining order): a feeder source (`Stdin`) and
    /// `KeepStdinOpen`. `Pty` is rejected too (either chaining order): a pseudo-terminal gives the child its
    /// own pty slave/ConPTY input as stdin, leaving nothing for the parent's own standard input to attach
    /// to. For the same reason `RunningProcess.TakeStdin` yields `None` for an inherited-stdin child (there
    /// is no interactive pipe to hand out). The capture/streaming verbs are unaffected — only the child's
    /// stdin wiring changes. Repeatable: a retry or a supervisor restart re-inherits the parent's stdin, so
    /// `InheritStdin` is never refused by the one-shot-source retry guard.
    member _.InheritStdin() =
        CommandConfig.ensureInheritStdinCompatible config

        Command(
            { config with
                StdinSource = Some Stdin.Inherit }
        )

    /// Keep the child's stdin pipe open after the source (if any) is exhausted, for interactive writing via
    /// `RunningProcess.TakeStdin`. Works both with **no** source (the pipe is interactive from the start —
    /// `TakeStdin` is available immediately) and **with** a `Command.Stdin(source)` (the source is fed
    /// first, the pipe is left open afterwards, and `TakeStdin` becomes available once that feed has
    /// finished — so the source and the interactive writer never write the pipe concurrently). Rejected
    /// (`ArgumentException`) when `InheritStdin` is already set — an inherited stdin has no pipe to keep open.
    ///
    /// **Take the writer before driving the handle to completion.** The kept-open pipe has exactly one
    /// owner, and `TakeStdin`/`TakeStdinAsync` is not its only claimant: a verb that runs the handle to
    /// completion while the writer is still untaken ends the child's input itself (`OutputStringAsync`/
    /// `OutputBytesAsync`/`WaitAsync`/`ProfileAsync`, a `WaitAnyAsync`/`WaitAllAsync`/`StopAsync` that is the
    /// handle's first consumer, and the verbs that never hand out a `RunningProcess` at all — `RunAsync`/
    /// `ExitCodeAsync`/`ProbeAsync`/`ParseAsync`/`OutputJsonAsync`/`FirstLineAsync`). That is what keeps a
    /// child reading stdin to EOF from hanging such a verb, and it is one-way: `TakeStdin` afterwards
    /// answers `None`. A writer already taken stays the caller's — no verb closes a handle it gave away.
    member _.KeepStdinOpen() =
        CommandConfig.ensureNoStdinInherit config "KeepStdinOpen"
        Command({ config with KeepStdinOpen = true })

    /// Set how the child's standard output is connected (default `Piped`). This is a stdout *destination*
    /// setter, so it also clears any prior `StdoutToFile` redirect — the last destination in a chain wins.
    member _.Stdout(mode: StdioMode) =
        CommandConfig.ensureStdoutModeCompatible config mode

        let updated =
            { config with
                StdoutMode = mode
                StdoutFile = None }

        CommandConfig.ensureIdleTimeoutCompatible updated
        Command(updated)

    /// Set how the child's standard error is connected (default `Piped`). Also clears any prior
    /// `StderrToFile` redirect — the last destination in a chain wins (see `Stdout`).
    member _.Stderr(mode: StdioMode) =
        CommandConfig.ensureStderrModeCompatible config mode

        let updated =
            { config with
                StderrMode = mode
                StderrFile = None }

        CommandConfig.ensureIdleTimeoutCompatible updated
        Command(updated)

    /// Redirect the child's standard **output** straight to the file at `path`, at the OS level — the
    /// child is handed the open file as its stdout handle/fd ON THE SPAWN (Windows: an inheritable file
    /// handle in `STARTUPINFO`; POSIX: a file fd via a `posix_spawn` file action), with **zero** copying
    /// through the parent and **no** parent pump. The file therefore keeps growing even after the parent
    /// process (or a pump that would have drained a pipe) is gone — ideal for a long-lived service's log
    /// under a `Supervisor`. `append = false` creates the file (truncating an existing one); `append =
    /// true` appends to it. `path` must be non-null and must not contain an embedded NUL (`'\000'`); a bad
    /// path (missing directory, permission denied) fails the spawn with `ProcessError.Spawn`, not here.
    ///
    /// **There is then no parent-side stdout stream** — `ProcessResult.Stdout` is empty, the streaming
    /// stdout verbs yield nothing, and `OutputEvent.Stdout` is never produced, exactly as for
    /// `StdioMode.Null`/`Inherit` (the child's stdout does not reach the parent at all). Because of that,
    /// the knobs that need a parent-side stdout stream are rejected at the builder boundary with an
    /// `ArgumentException` (in either chaining order): `StdoutTee` and `OnStdoutLine`. `MergeStderr` is
    /// rejected too (it folds stderr into the stdout stream the parent observes, which is absent here), as
    /// is `Pty` (a pseudo-terminal replaces the child's stdio with one terminal device). What is **allowed**
    /// and useful: redirect stdout to a file while capturing stderr the ordinary way (`ProcessResult.Stderr`,
    /// `OnStderrLine`, `StderrTee`, the stderr streaming verbs all still work), or redirect **both** streams
    /// to files with `StderrToFile`. As a stdout destination this overrides — and is overridden by — a later
    /// `Stdout(mode)` in the same chain (the last destination wins).
    member _.StdoutToFile(path: string, append: bool) =
        ArgumentNullException.ThrowIfNull path
        CommandConfig.rejectEmbeddedNul (nameof path) path
        CommandConfig.ensureStdoutFileCompatible config

        let updated =
            { config with
                StdoutFile = Some(path, append) }

        CommandConfig.ensureIdleTimeoutCompatible updated
        Command(updated)

    /// Redirect the child's standard output straight to the file at `path`, creating it (truncating an
    /// existing one). Shorthand for `StdoutToFile(path, append = false)` — see that overload.
    member this.StdoutToFile(path: string) = this.StdoutToFile(path, false)

    /// Redirect the child's standard **error** straight to the file at `path`, at the OS level — the
    /// stderr mirror of `StdoutToFile`. The child is handed the open file as its stderr handle/fd on the
    /// spawn, with no parent pump, so the file outlives the parent. `append = false` creates/truncates,
    /// `true` appends. There is then no parent-side stderr stream (`ProcessResult.Stderr` empty,
    /// `OnStderrLine` never fires, `OutputEvent.Stderr` never produced), so `StderrTee`, `OnStderrLine`,
    /// `MergeStderr`, and `Pty` are rejected at the builder boundary (in either chaining order). Redirecting
    /// stderr to a file while capturing stdout normally — or redirecting both streams with `StdoutToFile` —
    /// is supported. See `StdoutToFile` for the full contract.
    member _.StderrToFile(path: string, append: bool) =
        ArgumentNullException.ThrowIfNull path
        CommandConfig.rejectEmbeddedNul (nameof path) path
        CommandConfig.ensureStderrFileCompatible config

        let updated =
            { config with
                StderrFile = Some(path, append) }

        CommandConfig.ensureIdleTimeoutCompatible updated
        Command(updated)

    /// Redirect the child's standard error straight to the file at `path`, creating it (truncating an
    /// existing one). Shorthand for `StderrToFile(path, append = false)` — see that overload.
    member this.StderrToFile(path: string) = this.StderrToFile(path, false)

    /// Encode text sent to the child's stdin with `encoding` (default UTF-8). This affects
    /// `Stdin.FromString`/`FromLines`/`FromAsyncLines` and `ProcessStdin.WriteLineAsync`; raw
    /// `Stdin.FromBytes` and `ProcessStdin.WriteAsync` remain byte-exact.
    member _.StdinEncoding(encoding: Encoding) =
        ArgumentNullException.ThrowIfNull encoding

        Command({ config with StdinEncoding = encoding })

    /// Use `timeProvider` for retry delays, readiness probes, `PtySession` pattern deadlines, and
    /// supervision. The default is `TimeProvider.System`; supplying a deterministic provider makes
    /// those time-dependent paths testable without changing process-wide time.
    member _.TimeProvider(timeProvider: TimeProvider) =
        ArgumentNullException.ThrowIfNull timeProvider

        Command(
            { config with
                TimeProvider = timeProvider }
        )

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

    /// Encode text stdin and decode both captured streams with `encoding`. For a legacy Windows console
    /// program — one whose non-ASCII input and output use a code page rather than UTF-8 — use
    /// `ConsoleEncoding()`, which resolves the right code page for the current host and applies it here.
    member _.Encoding(encoding: Encoding) =
        ArgumentNullException.ThrowIfNull encoding

        Command(
            { config with
                StdinEncoding = encoding
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

    /// Invoke `handler` for each captured stdout line, as it is pumped. Rejected (`ArgumentException`)
    /// when stdout is `Null`, `Inherit`, or redirected with `StdoutToFile`, because those destinations
    /// leave no parent-side stdout stream for the handler to observe.
    member _.OnStdoutLine(handler: Action<string>) =
        ArgumentNullException.ThrowIfNull handler
        CommandConfig.ensureStdoutPiped config "OnStdoutLine"
        CommandConfig.ensureNoStdoutFile config "OnStdoutLine"

        Command(
            { config with
                OnStdoutLine = Some handler }
        )

    /// Invoke `handler` for each captured stderr line, as it is pumped. Rejected (`ArgumentException`)
    /// when stderr is `Null`, `Inherit`, redirected with `StderrToFile`, or folded into stdout with
    /// `MergeStderr`, because those configurations leave no separate parent-side stderr stream.
    member _.OnStderrLine(handler: Action<string>) =
        ArgumentNullException.ThrowIfNull handler
        CommandConfig.ensureStderrPiped config "OnStderrLine"
        CommandConfig.ensureNoMergeStderr config "OnStderrLine"
        CommandConfig.ensureNoPty config "OnStderrLine"
        CommandConfig.ensureNoStderrFile config "OnStderrLine"

        Command(
            { config with
                OnStderrLine = Some handler }
        )

    /// Copy raw captured stdout bytes to `sink` (a tee), in addition to capture. Rejected
    /// (`ArgumentException`) when stdout is `Null`, `Inherit`, or redirected with `StdoutToFile`, because
    /// those destinations leave no parent-side stdout stream to tee.
    member _.StdoutTee(sink: Stream) =
        ArgumentNullException.ThrowIfNull sink
        CommandConfig.ensureStdoutPiped config "StdoutTee"
        CommandConfig.ensureNoStdoutFile config "StdoutTee"
        Command({ config with StdoutTee = Some sink })

    /// Copy raw captured stderr bytes to `sink` (a tee), in addition to capture. Rejected
    /// (`ArgumentException`) when stderr is `Null`, `Inherit`, redirected with `StderrToFile`, or folded
    /// into stdout with `MergeStderr`, because those configurations leave no separate parent-side stream.
    member _.StderrTee(sink: Stream) =
        ArgumentNullException.ThrowIfNull sink
        CommandConfig.ensureStderrPiped config "StderrTee"
        CommandConfig.ensureNoMergeStderr config "StderrTee"
        CommandConfig.ensureNoPty config "StderrTee"
        CommandConfig.ensureNoStderrFile config "StderrTee"
        Command({ config with StderrTee = Some sink })

    /// Merge the child's standard **error** into its standard **output** at the OS level — the library
    /// equivalent of a shell `2>&1`. The native spawn points the child's stderr at the very same
    /// pipe/handle as its stdout (POSIX `dup2` of fd 2 onto stdout's target; Windows shares one handle
    /// across `STARTUPINFO.hStdOutput`/`hStdError`), so the two streams interleave **honestly, byte for
    /// byte** on the single stdout stream — the real terminal-order `2>&1` view that the post-hoc
    /// `ProcessResult.Combined` (a concatenation of two *separately* captured streams) cannot reproduce.
    /// It works uniformly for the buffering verbs, the streaming verbs (`StdoutLinesAsync`/
    /// `OutputEventsAsync`), and pipeline stages. The default is off (separate stdout/stderr, unchanged).
    ///
    /// **There is then no separate stderr stream, and the API reflects that honestly** (never a silent
    /// downgrade): `ProcessResult.Stderr` is always empty, the streamed stderr stream is absent, and
    /// `OutputEventsAsync` emits only `OutputEvent.Stdout` events — the stderr lines already live, in
    /// order, in the stdout byte stream. Because the merge removes the separate stream, the
    /// separate-stderr **observation** knobs are rejected at the builder boundary with an
    /// `ArgumentException` (in either chaining order) rather than silently never firing: `StderrTee` and
    /// `OnStderrLine` cannot be combined with `MergeStderr`. The remaining stderr knobs are documented
    /// **no-ops** under merge — the merged bytes follow stdout's settings: `StderrEncoding` (the merged
    /// stream decodes with `StdoutEncoding`), `StderrLineTerminator` (framed with `StdoutLineTerminator`),
    /// and the `Stderr` `StdioMode` (stderr follows stdout's destination). These are not rejected because
    /// `Encoding()` and `LineTerminator()` set the stdout+stderr pair together, so rejecting them would
    /// make those pair setters conflict with `MergeStderr`.
    ///
    /// Inside a `Pipeline`, `MergeStderr` is allowed only on the **last** stage — its stdout is the
    /// pipeline's captured output, so a `2>&1` there captures the final stage's merged output. Setting it
    /// on any earlier stage is rejected (`ArgumentException`) the moment the stage stops being last: a
    /// pipeline wires each stage's stdout into the next stage's stdin, so merging an intermediate stage's
    /// stderr would inject it into the downstream stage's input data.
    member _.MergeStderr() =
        CommandConfig.ensureNoMergeStderrObservers config
        CommandConfig.ensureNoFileRedirect config "MergeStderr"

        let updated = { config with MergeStderr = true }
        CommandConfig.ensureIdleTimeoutCompatible updated
        Command(updated)

    /// Bound the in-memory backlog of captured lines.
    member _.OutputBuffer(policy: OutputBufferPolicy) =
        ArgumentNullException.ThrowIfNull policy
        Command({ config with OutputBuffer = policy })

    /// Shape every decoded line on its way into the in-memory capture backlog — the
    /// redaction-at-capture seam. `policy.OnCapture` runs before a line is retained, so what it returns
    /// is what `ProcessResult.Stdout`/`Stderr` and `Finished.Stderr` carry; a policy that throws or
    /// returns `null` fails **closed** (that line is retained empty, never raw).
    ///
    /// It shapes the retained backlog **only**. The per-line handlers (`OnStdoutLine`/`OnStderrLine`),
    /// the tees (`StdoutTee`/`StderrTee`), the streaming verbs and a raw byte capture
    /// (`OutputBytesAsync`'s stdout, a pipeline's captured stdout) all keep seeing the unshaped line —
    /// see `ICapturePolicy` for the whole boundary and why it is drawn there. Unset (the default)
    /// retains exactly what the child wrote. Composes with `OutputBuffer`, which decides how much of
    /// the shaped output survives; the last `CapturePolicy` call in a chain wins.
    member _.CapturePolicy(policy: ICapturePolicy) =
        ArgumentNullException.ThrowIfNull policy

        Command(
            { config with
                CapturePolicy = Some policy }
        )

    /// The `ICapturePolicy.Name` of the policy configured with `CapturePolicy`, or `None` when none is
    /// set — the introspection point that keeps a configured policy visible (in a test assertion, a
    /// diagnostic dump) instead of an anonymous callback. Deliberately the *name* rather than the
    /// policy itself: a name is safe to print, a policy object is not something a diagnostic should
    /// hand out.
    member _.ConfiguredCapturePolicyName: string option =
        config.CapturePolicy |> Option.map (fun policy -> policy.Name)

    /// Opt in to a bounded/backpressure channel for the streaming verbs (`StdoutLinesAsync`/
    /// `OutputEventsAsync`/`WaitForLineAsync`) and `ContentLengthSession.FramesAsync` — which honours only
    /// the two lossless full modes and refuses `DropOldest`/`DropNewest`, since a dropped protocol frame is
    /// corruption its consumer could never detect. It is
    /// inapplicable to `PtySession`, whose window and transcript have their own character bounds.
    /// Unset (the default) keeps the unbounded streaming channel ProcessKit has always used. See
    /// <a href="https://zelanton.github.io/ProcessKit-fSharp/streaming.html">Streaming</a> for the backpressure
    /// deadlock footgun before opting in to `StreamFullMode.Backpressure`.
    member _.StreamBuffer(policy: StreamBufferPolicy) =
        ArgumentNullException.ThrowIfNull policy

        Command(
            { config with
                StreamBuffer = Some policy }
        )

    /// The configured run timeout, if any.
    member _.ConfiguredTimeout = config.Timeout

    /// Kill the run after `duration`, reporting the result as `Outcome.TimedOut`. The deadline bounds
    /// the run's total wall time and is measured from the **spawn**, so a live `StartAsync` handle
    /// collected later gets only what is left of it, and one already past its deadline is killed as
    /// soon as it is collected — after at most a quarter-second settle window, itself never longer than
    /// `duration`, in which an exit the child had already made can still surface. That window is why a
    /// child that finished on its own inside the deadline still reports its real outcome, however
    /// little of the budget was left when the collecting verb arrived. A fired deadline always reports
    /// this configured `duration`. A negative `duration` is rejected; one larger than ~24.8 days is
    /// treated as no timeout.
    member _.Timeout(duration: TimeSpan) =
        ArgumentOutOfRangeException.ThrowIfLessThan(duration, TimeSpan.Zero)
        Command({ config with Timeout = Some duration })

    /// On timeout, send the configured `StopSignal` and force-kill only if still alive after `grace`.
    /// On Windows the default signal uses the documented best-effort soft phase before the Job kill;
    /// non-default stop signals are refused at spawn. A negative `grace` is rejected
    /// (`ArgumentOutOfRangeException`), matching `Timeout`.
    member _.TimeoutGrace(grace: TimeSpan) =
        ArgumentOutOfRangeException.ThrowIfLessThan(grace, TimeSpan.Zero)

        Command(
            { config with
                TimeoutGrace = Some grace }
        )

    /// Choose the soft signal sent by graceful stop paths before they escalate to a hard kill.
    /// The default is `Signal.Term`. Windows refuses non-default values at spawn because it cannot
    /// faithfully represent arbitrary POSIX signals; its existing WM_CLOSE/CTRL+BREAK mechanisms remain
    /// available through the documented Windows control APIs.
    member _.StopSignal(signal: Signal) =
        SignalValidation.gracefulStop (nameof signal) signal
        Command({ config with StopSignal = signal })

    /// Kill the run when it produces **no output** — on neither stdout nor stderr — for `duration`,
    /// reporting the result as `Outcome.TimedOut`. Every chunk of output resets the deadline, so a run
    /// that keeps streaming stays alive; one that hangs after going quiet is killed. This is distinct
    /// from `Timeout`, which bounds the *total* run length regardless of output: the two are
    /// independent and may both be set, each firing on its own condition. Idle activity is measured at
    /// byte granularity across every verb (buffered capture, streaming, raw bytes, and the drained
    /// `WaitAsync`/`ProfileAsync`), so output discarded by a parent-side verb — or a single long
    /// newline-free blob — still counts as active. At least one effective parent-side output stream is
    /// required: combining this with only `Null`, `Inherit`, or direct file destinations is rejected with
    /// `ArgumentException` in either chaining order; a PTY's merged master stream is observable. A
    /// negative `duration` is rejected
    /// (`ArgumentOutOfRangeException`, matching `Timeout`); one larger than ~24.8 days is treated as no
    /// idle deadline. Honours `TimeoutGrace` (a graceful stop, then a hard kill) exactly as `Timeout`.
    member _.IdleTimeout(duration: TimeSpan) =
        ArgumentOutOfRangeException.ThrowIfLessThan(duration, TimeSpan.Zero)

        let updated =
            { config with
                IdleTimeout = Some duration }

        CommandConfig.ensureIdleTimeoutCompatible updated
        Command(updated)

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
    ///
    /// The teardown is an **immediate hard kill** by default; add `CancelGrace` (and optionally
    /// `CancelSignal`) to route it through a soft signal → grace → hard kill ladder instead. The outcome
    /// is unchanged either way — a cancelled run is always `ProcessError.Cancelled`.
    member _.CancelOn(cancellationToken: CancellationToken) =
        Command(
            { config with
                CancelOn = Some cancellationToken }
        )

    /// Make a **cancellation** graceful: when the token that cancels this run fires, its tree is sent
    /// `CancelSignal` (default `Signal.Term`), given up to `grace` to leave on its own, and only then
    /// hard-killed — the cancellation mirror of `TimeoutGrace`, for the "one shared token, cancelled on
    /// Ctrl-C" shutdown pattern where every child would otherwise be killed outright.
    ///
    /// **Opt-in, and off by default.** Without it a cancellation hard-kills the tree at once, exactly as
    /// before. It applies to every cancellation source a run has — the verb's own `CancellationToken`,
    /// this command's `CancelOn` (including one inherited from `CliClient.WithDefaults`),
    /// `Pipeline.CancelOn` (set it on stage 0, which owns the pipeline-wide control configuration), and a
    /// `Supervisor` incarnation's cancellation — and to buffered and streamed completion verbs alike. The
    /// streamed one, `FirstLineAsync`, therefore waits for the ladder to conclude before it answers
    /// `Cancelled` — returning is what reaps its tree, so answering sooner would collapse the very window
    /// this knob opens. The wait is bounded by `grace`; without this knob it answers immediately, as before.
    ///
    /// **The outcome does not change: a cancelled run is still always an error.** Every consuming path
    /// still reports `ProcessError.Cancelled`, whether the child left on the soft signal or was killed
    /// after the grace; only the manner of the goodbye becomes gentler, so a child that must flush state,
    /// remove a pidfile, or finish a transaction gets the chance to.
    ///
    /// **Independent of `Timeout`/`TimeoutGrace`/`StopSignal`,** whose behaviour is untouched: a deadline
    /// that expires still uses `TimeoutGrace`/`StopSignal` (or hard-kills when unset), and neither pair
    /// gap-fills the other. It needs no `Timeout` of its own.
    ///
    /// **Scope, like the rest of cancellation:** a run that owns its group tears down the whole tree; a
    /// run sharing a `ProcessGroup` reaches only its own direct child (the documented shared-group
    /// teardown gap). On **Windows** the soft tier is the documented best-effort one — a `WM_CLOSE` to a
    /// windowed child plus a CTRL+BREAK to a child started with `WindowsCtrlSignals()` — and the hard kill
    /// still lands when the grace elapses; a non-default `CancelSignal` is refused at spawn rather than
    /// silently downgraded. A negative `grace` is rejected (`ArgumentOutOfRangeException`), matching
    /// `TimeoutGrace`; `TimeSpan.Zero` escalates immediately.
    member _.CancelGrace(grace: TimeSpan) =
        ArgumentOutOfRangeException.ThrowIfLessThan(grace, TimeSpan.Zero)

        Command({ config with CancelGrace = Some grace })

    /// Choose the soft signal that opens a `CancelGrace` window. The default is `Signal.Term`, exactly
    /// like `StopSignal`'s. Deliberately independent of `StopSignal`: a command may want a different
    /// farewell for "the caller changed its mind" than for "the deadline expired", and neither knob
    /// gap-fills the other.
    ///
    /// Inert without `CancelGrace` — there is no soft tier to send it on. Windows refuses a non-default
    /// value at spawn with `ProcessError.Unsupported` (it cannot faithfully represent an arbitrary POSIX
    /// signal), exactly as `StopSignal` does, never a silent downgrade to the hard kill.
    member _.CancelSignal(signal: Signal) =
        SignalValidation.gracefulStop (nameof signal) signal

        Command(
            { config with
                CancelSignal = Some signal }
        )

    /// Run the command up to `maxAttempts` times **in total** (the initial run plus up to
    /// `maxAttempts - 1` retries), waiting `delay` between attempts, while `shouldRetry` returns true
    /// for the error. `maxAttempts` of `0` or `1` both mean a single run — a command always runs at
    /// least once. A negative `delay` is rejected; delays beyond the maximum armable timer interval
    /// are clamped when the retry runs. If `shouldRetry` throws, the current attempt is terminal and
    /// the consuming verb returns `ProcessError.RetryPredicate` with the original `ProcessError` in
    /// `Original`; the callback exception never escapes as a raw task fault and no further attempt runs.
    member _.Retry(maxAttempts: int, delay: TimeSpan, shouldRetry: Func<ProcessError, bool>) =
        ArgumentNullException.ThrowIfNull shouldRetry
        ArgumentOutOfRangeException.ThrowIfLessThan(delay, TimeSpan.Zero)

        Command(
            { config with
                Retry = Some(maxAttempts, RetryDelayPolicy.Fixed delay, shouldRetry)
                // A fresh retry policy re-opts-in, undoing an earlier `RetryNever` in the same chain.
                RetryDisabled = false }
        )

    /// Run the command up to `maxAttempts` times in total, using exponential backoff before each
    /// retry: `baseDelay × factor^n` (starting at `n = 0`), capped at `maxDelay` before optional
    /// jitter multiplies it by a random factor in `[0.5, 1.5)`. All delays must be non-negative;
    /// `factor` must be finite and at least `1.0`. Retry timers use the command's `TimeProvider`.
    /// If `shouldRetry` throws, the current attempt is terminal and the consuming verb returns
    /// `ProcessError.RetryPredicate` with the original `ProcessError` in `Original`; the callback
    /// exception never escapes as a raw task fault and no further attempt runs.
    member _.RetryBackoff
        (
            maxAttempts: int,
            baseDelay: TimeSpan,
            factor: float,
            maxDelay: TimeSpan,
            jitter: bool,
            shouldRetry: Func<ProcessError, bool>
        ) =
        ArgumentNullException.ThrowIfNull shouldRetry
        ArgumentOutOfRangeException.ThrowIfLessThan(baseDelay, TimeSpan.Zero)
        ArgumentOutOfRangeException.ThrowIfLessThan(maxDelay, TimeSpan.Zero)

        if not (Double.IsFinite factor) || factor < 1.0 then
            raise (ArgumentOutOfRangeException(nameof factor, factor, "factor must be finite and at least 1.0"))

        Command(
            { config with
                Retry =
                    Some(maxAttempts, RetryDelayPolicy.Exponential(baseDelay, factor, maxDelay, jitter), shouldRetry)
                RetryDisabled = false }
        )

    /// Explicitly disable retrying for this command, overriding any `Retry` policy already on it —
    /// including one inherited from a `CliClient.WithDefaults` template. Distinct from never having
    /// called `Retry`/`RetryBackoff` at all: an unset policy still accepts a client's default,
    /// `RetryNever` refuses it. The command always runs exactly once. A later retry-policy call in the
    /// same chain re-opts back in (the last call wins).
    member _.RetryNever() =
        Command({ config with RetryDisabled = true })

    /// Add a POSIX full-duplex channel at child file descriptor `targetFd` (3 or greater). After
    /// `StartAsync`, claim the parent side once with `RunningProcess.TakeExtraFd(targetFd)`. Windows
    /// reports `ProcessError.Unsupported`; pipelines, detached launches, and test doubles reject the
    /// setting rather than silently dropping it.
    member _.ExtraFd(targetFd: int) =
        ArgumentOutOfRangeException.ThrowIfLessThan(targetFd, 3)

        if config.ExtraFds.Contains targetFd then
            raise (ArgumentException($"extra file descriptor {targetFd} is already configured", nameof targetFd))

        Command(
            { config with
                ExtraFds = config.ExtraFds.Add targetFd }
        )

    /// Inside a pipeline, do not let this stage's non-zero exit fail the pipeline (it is still
    /// reported in the stage outcomes). Outside a pipeline this flag has no effect.
    member _.UncheckedInPipe() =
        Command({ config with UncheckedInPipe = true })

    /// Replace the set of exit codes treated as success (the default is `{0}`) — this is what
    /// `ProcessResult.IsSuccess`, `ensureSuccess`, and the `RunAsync` verbs check. The codes *replace* the
    /// default rather than adding to it, so pass `[0; 3]` to accept both `0` and `3`. An empty set has no
    /// meaningful semantics — no exit could ever count as success — so it is rejected at the builder
    /// boundary with `ArgumentException`, matching every other builder knob that fails loud on an invalid
    /// value rather than silently keeping the previous codes. Pass at least one code.
    member _.OkCodes(codes: seq<int>) =
        ArgumentNullException.ThrowIfNull codes
        let list = List.ofSeq codes

        if List.isEmpty list then
            raise (
                ArgumentException(
                    "the ok-codes set must not be empty — at least one exit code must count as success",
                    nameof codes
                )
            )

        Command({ config with OkCodes = list })

    /// Windows: run the child with `CREATE_NO_WINDOW`, so a console child spawned from a GUI app
    /// does not flash a console window. No effect on Unix.
    member _.CreateNoWindow() =
        Command({ config with CreateNoWindow = true })

    /// Windows: register the child leader for targeted console signalling, so that
    /// `ProcessGroup.Signal(Signal.Int)` / `Signal.Term` can deliver it a best-effort console
    /// **CTRL+BREAK** — the closest Windows analogue to a graceful `SIGINT`/`SIGTERM` — instead of the
    /// hard atomic Job-Object kill, giving a console child a chance to clean up. **Best-effort and
    /// console-only:** the event reaches only a console child that shares the caller's console. A child
    /// given its own or hidden console (via `CreateNoWindow`), or a parent that has no console at all,
    /// cannot receive it — the send then fails honestly with `ProcessError.Unsupported` rather than a
    /// silent downgrade — and even on a successful send delivery is not guaranteed (the child may
    /// install its own console handler). `Signal.Kill` is unaffected (always the atomic Job kill), and
    /// this has no effect on Unix, where signals reach the child's process group regardless. Regular children
    /// get `CREATE_NEW_PROCESS_GROUP` through this option. ConPTY children always receive that flag for
    /// isolation, regardless of this option, and Windows consequently disables their default CTRL+C handling:
    /// sending U+0003 through ConPTY input does not interrupt the child. For ConPTY, this option only registers
    /// the leader for targeted CTRL+BREAK; it does not control creation of the process group.
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

    /// Set the **Linux I/O-scheduling priority** of the child — and of the tree it spawns — so that
    /// background disk work yields to the interactive users of the same device. A separate axis from the
    /// CPU-scheduling `Priority` above and not a substitute for it: that one decides how much *processor*
    /// the child gets, this one how its *block-device* requests are ordered. Build the value with
    /// `IoPriority.Idle` / `IoPriority.BestEffort level` / `IoPriority.RealTime level`, which validate the
    /// level at construction; the default (unset) leaves the inherited I/O priority untouched. Last write
    /// wins, like every other builder knob.
    ///
    /// **How far it reaches, and when it takes effect.** The kernel copies the spawning task's I/O
    /// priority into the child when the child is created and it survives every `exec`, so the priority is
    /// in force for the child's very FIRST block-device request — before its program runs — and is
    /// inherited by every descendant the child later forks. There is no spawn-then-apply window here (the
    /// one the CPU `Priority` axis documents for its post-spawn `setpriority`), and no helper binary is
    /// involved, so it composes unchanged with a `Uid`/`Gid` drop, a `Pty`, a cgroup-contained group, a
    /// `Command.Rlimit` set, and `Command.Arg0` alike.
    ///
    /// **Linux-only, and honestly so.** `ioprio_set(2)` is a Linux system call with no Win32 or POSIX
    /// equivalent, so a spawn carrying an I/O priority fails with `ProcessError.Unsupported` on
    /// **Windows, macOS, and the BSDs** rather than running the child at the inherited priority as if
    /// the request had been honoured. `Command.LaunchDetached` refuses it on the same terms — that verb
    /// deliberately gives up ownership of the child, and this is an owner-applied setting.
    ///
    /// **Privilege.** `Idle` and `BestEffort` need none. `RealTime` needs `CAP_SYS_ADMIN` (Linux ≥ 5.14
    /// accepts `CAP_SYS_NICE` too); without it the kernel refuses the request and the spawn fails with
    /// `ProcessError.Spawn` — never a silent downgrade to best-effort. A kernel or sandbox with no
    /// `ioprio_set` at all (a seccomp filter returning `ENOSYS`) is a typed `ProcessError.Unsupported`.
    ///
    /// **What it does not promise.** The class and level are recorded on the child unconditionally, but
    /// whether they change the *order requests are served in* is the block device's I/O scheduler's
    /// decision: Linux honours I/O priorities under **BFQ** (and the historical CFQ), while `mq-deadline`,
    /// `kyber`, and `none` — the common defaults for NVMe — largely ignore them. This knob asks the
    /// kernel for a priority; it cannot promise a scheduler that acts on it. See `IoPriority`.
    member _.IoPriority(priority: IoPriority) =
        ArgumentNullException.ThrowIfNull(priority, nameof priority)

        Command(
            { config with
                IoPriority = Some priority }
        )

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
        CommandConfig.ensureNoWindowsTokenHardening config "Umask"
        Command({ config with Umask = Some mask })

    /// Cap one Unix **per-process** resource for the child (`setrlimit(2)`): `soft` is the value in
    /// force, `hard` the ceiling the child may raise its own soft value back up to. Both are in the
    /// resource's own native unit — bytes, seconds, or a count, as `RlimitResource` documents. The cap
    /// is applied before the child's program starts and is inherited INDIVIDUALLY by each descendant
    /// (each gets its own copy of the cap, not a shared budget); a descendant may lower its own limits
    /// further, and may raise its soft value again as far as the inherited hard value. That makes this
    /// a robustness bound, not a containment boundary — the whole-tree caps of
    /// `ProcessGroupOptions`/`ResourceLimits` are the boundary, and the two compose.
    ///
    /// Calls for DIFFERENT resources accumulate; repeating the same resource replaces the earlier pair
    /// in place (last write wins), like every other builder knob. `soft` and `hard` must be
    /// non-negative and `soft` must not exceed `hard` — both rejected with
    /// `ArgumentOutOfRangeException` here at the builder boundary rather than deep in a native call.
    /// There is deliberately no "unlimited" value: this knob exists to LOWER what the child inherited,
    /// and raising a hard limit above the inherited one needs privilege (see the refusal note below).
    ///
    /// **Precedence with the whole-tree `ResourceLimits.CpuTimeMax`.** Both target CPU time when a
    /// `Rlimit(RlimitResource.Cpu, ...)` runs inside a group that also sets `CpuTimeMax`. Neither wins
    /// by position: the STRICTER of the two is applied on each of the soft and hard values (the smaller
    /// number), so adding one can only ever tighten the effective cap, never relax the other. They are
    /// applied ONCE, together, by the same pre-exec step, so the looser value can never silently
    /// overwrite the tighter one on the way to the child.
    ///
    /// **Unix-only, and honestly so.** On **Windows** there is no `setrlimit` analogue, so a spawn
    /// carrying any rlimit fails with `ProcessError.Unsupported` — never a child running uncapped.
    /// On **POSIX** the limits are applied by the util-linux `prlimit` helper, which sets them on
    /// itself and then `exec`s the real program in place (same pid, so containment, `Priority`, and a
    /// PTY are all unaffected). Like `setpriv` and `setsid --ctty`, that helper is loaded only from a
    /// trusted system directory (`/usr/bin`, `/bin`, `/usr/sbin`, `/sbin`) and never from `PATH`; a
    /// host holding it in none of them — macOS/BSD, which have no util-linux, or a minimal image —
    /// fails the spawn with `ProcessError.ResourceLimit` rather than dropping the caps. A limit the
    /// kernel itself refuses (raising a hard limit above the inherited one without privilege) fails the
    /// helper before it `exec`s anything, so the child never runs with the cap silently unapplied; the
    /// helper's message reaches the run's stderr.
    ///
    /// Because the helper `exec`s the target BY NAME it has no seam for a distinct `argv[0]`, so
    /// combining this with `Command.Arg0` is refused at spawn with `ProcessError.Unsupported` —
    /// exactly as `Arg0` is refused alongside the `setpriv`/`setsid --ctty`/`CpuTimeMax` shims, and for
    /// the same reason: the override would otherwise land on the helper's own `argv[0]` instead of the
    /// program that was actually asked for.
    member _.Rlimit(resource: RlimitResource, soft: int64, hard: int64) =
        ArgumentOutOfRangeException.ThrowIfNegative(soft, nameof soft)
        ArgumentOutOfRangeException.ThrowIfNegative(hard, nameof hard)

        if soft > hard then
            raise (
                ArgumentOutOfRangeException(
                    nameof soft,
                    soft,
                    $"the {resource.Name} rlimit soft value ({soft}) must not exceed its hard value ({hard})"
                )
            )

        let limit = Rlimit(resource, soft, hard)

        let existing =
            config.Rlimits |> Seq.tryFindIndex (fun current -> current.Resource = resource)

        let updated =
            match existing with
            | Some index -> config.Rlimits.SetItem(index, limit)
            | None -> config.Rlimits.Add limit

        Command({ config with Rlimits = updated })

    /// Run the child under this Unix user id (`setuid`). **Unix-only:** on Windows (which has no
    /// equivalent) a requested uid fails the spawn with `ProcessError.Unsupported` rather than being
    /// silently ignored. Because `posix_spawn` has no uid attribute, a command with a uid (or `Gid`) is
    /// spawned through the `setpriv` helper (util-linux), which drops the gid/uid and clears the
    /// supplementary groups before `exec`ing the real program in place. Dropping to another user is
    /// **root-only** (`euid == 0`): a non-root caller asking for a different uid fails the spawn with
    /// `ProcessError.Spawn` (never a child that kept the parent's uid) — including one holding
    /// `CAP_SETUID`/`CAP_SETGID`, which the up-front check conservatively refuses rather than probes.
    ///
    /// **Where the helper comes from.** `setpriv` performs the drop while still running with the parent's
    /// (usually root) credentials, so it is never resolved on `PATH`: it is loaded only from a fixed list
    /// of trusted system directories — `/usr/bin`, `/bin`, `/usr/sbin`, `/sbin`, in that order — and
    /// launched by the absolute path of the match. A host that holds `setpriv` in **none** of them fails
    /// the spawn with the same typed `ProcessError.Spawn`, *even when `setpriv` is present on the `PATH`*:
    /// mainstream Linux installs it in a trusted directory (Debian/Ubuntu and Fedora in `/usr/bin`,
    /// Alpine's `util-linux` in `/bin`), a non-FHS layout such as NixOS or Guix does not, and macOS/BSD
    /// have no util-linux at all. `Command.PreferLocal` never applies to the helper either — it
    /// substitutes your own target program. See `docs/hardening.md`, "Where the Unix helper binaries come
    /// from".
    ///
    /// `uid` must be non-negative (rejected with `ArgumentOutOfRangeException` at the builder boundary).
    /// Pair with `Gid` (or `User`) for a full drop.
    member _.Uid(uid: int) =
        ArgumentOutOfRangeException.ThrowIfNegative uid
        CommandConfig.ensureNoWindowsTokenHardening config "Uid"
        Command({ config with Uid = Some uid })

    /// Run the child under this Unix group id (`setgid`) — see `Uid` for the mechanism, platform notes,
    /// and privilege requirement. `setgid` is applied before any `setuid`, so the two compose into a
    /// correct privilege drop. `gid` must be non-negative.
    member _.Gid(gid: int) =
        ArgumentOutOfRangeException.ThrowIfNegative gid
        CommandConfig.ensureNoWindowsTokenHardening config "Gid"
        Command({ config with Gid = Some gid })

    /// Run the child under this Unix user **and** group id — the common privilege-drop pair, equivalent
    /// to `.Gid(gid).Uid(uid)`. See `Uid` for the mechanism, ordering (`setgid` before `setuid`),
    /// supplementary-group clearing, platform notes, and privilege requirement. Both ids must be
    /// non-negative.
    member _.User(uid: int, gid: int) =
        ArgumentOutOfRangeException.ThrowIfNegative uid
        ArgumentOutOfRangeException.ThrowIfNegative gid
        CommandConfig.ensureNoWindowsTokenHardening config "User"

        Command(
            { config with
                Uid = Some uid
                Gid = Some gid }
        )

    /// Set the child's Unix **supplementary groups**, *replacing* the inherited set — the missing third
    /// leg of a correct privilege drop, next to `Uid`/`Gid`. A bare `Uid`/`Gid`/`User` drop *clears* the
    /// parent's supplementary groups (`setpriv --clear-groups`) so the child never keeps root's; pass the
    /// target user's groups here to grant them back (e.g. a service user's `docker`/`video`/`adm`
    /// membership), or `[]` to keep the cleared default explicitly. The gids are applied verbatim — they
    /// need not name existing `/etc/group` entries. Because it rides the same `setpriv` helper as the
    /// uid/gid drop (mapped to `setpriv --groups`), it is meaningful only **alongside a `Uid` or `Gid`
    /// drop**: `Groups` set without either is refused at spawn with `ProcessError.Spawn` rather than
    /// silently ignored (never a silent no-op) — and it inherits that helper's trusted-directory
    /// resolution, so a host carrying `setpriv` only on its `PATH` refuses this drop too (see `Uid`).
    /// **Unix-only:** on Windows (no equivalent) a set value fails the spawn with
    /// `ProcessError.Unsupported`, exactly like `Uid`/`Gid`. Every gid must be
    /// non-negative — rejected with `ArgumentOutOfRangeException` at the builder boundary, naming the
    /// offending element by index (`Groups[2]`).
    member _.Groups(gids: seq<int>) =
        ArgumentNullException.ThrowIfNull gids
        CommandConfig.ensureNoWindowsTokenHardening config "Groups"
        let materialized = gids |> Seq.toArray

        materialized
        |> Array.iteri (fun index gid ->
            if gid < 0 then
                raise (
                    ArgumentOutOfRangeException(
                        $"Groups[{index}]",
                        gid,
                        "a supplementary group id must be non-negative"
                    )
                ))

        Command(
            { config with
                Groups = Some(List.ofArray materialized) }
        )

    /// Detach the child into a **new session** (`setsid()`): its own session and process group, with no
    /// controlling terminal. **Unix-only:** on Windows a requested detach fails the spawn with
    /// `ProcessError.Unsupported`. `setsid()` makes the child a new process-group leader (pgid == pid),
    /// so the kill-on-drop group teardown (`killpg`) still reaches the whole session — containment is
    /// preserved; the new session simply replaces the group's default `POSIX_SPAWN_SETPGROUP` for this
    /// command. A `setsid()` the OS refuses fails the spawn with `ProcessError.Spawn`.
    member _.Setsid() =
        CommandConfig.ensureNoPtyForSetsid config
        CommandConfig.ensureNoWindowsTokenHardening config "Setsid"
        Command({ config with Setsid = true })

    /// Override the child's **`argv[0]`** independently of the executable that is actually launched
    /// (`Program`) — the mechanism behind multicall binaries such as BusyBox/Toybox (which dispatch on
    /// their own `argv[0]`) and the login-shell convention of a leading `-` (`-bash`). Only the argument
    /// vector the child observes changes: `Program` alone still drives PATH/`PreferLocal` resolution,
    /// preflight, spawn diagnostics (`ProcessError`), and containment — exactly as if `Arg0` had never
    /// been called. Repeated calls are last-write-wins, like every other builder knob.
    ///
    /// `arg0` must be non-empty and must not contain an embedded NUL (`'\000'`) — both rejected with
    /// `ArgumentException` at the builder boundary rather than reaching the native layer, where an empty
    /// or NUL-truncated value could let the observed `argv[0]` silently diverge from what was requested.
    ///
    /// **Unix-only**, applied on the POSIX spawn path by handing the native layer a distinct `argv[0]`
    /// separate from the file `posix_spawnp` resolves and executes. **Windows** has no separate `argv[0]`
    /// contract (`CreateProcessW` takes one raw command line, not an argv array with an independent first
    /// element), so a set value fails the spawn there with `ProcessError.Unsupported`, never a silent
    /// fallback to `Program`. It is likewise refused with `ProcessError.Unsupported` — at spawn time, on
    /// POSIX — when combined with a knob that routes the launch through a helper which must re-`exec` the
    /// target BY NAME and has no CLI seam of its own for a distinct `argv[0]`: a `Uid`/`Gid`/`Groups`/
    /// `KillOnParentDeath` privilege/parent-death drop (the `setpriv` helper), `Pty` (the `setsid --ctty`
    /// helper), a run under `ProcessGroup`'s Linux cgroup backend (the `/bin/sh` migration launcher), or a
    /// `ResourceLimits.CpuTimeMax` run on the POSIX process-group mechanism (the `/bin/sh` `RLIMIT_CPU`
    /// shim). Honouring the override there would mean either handing it to the WRONG process (the helper's
    /// own `argv[0]`, silently discarding what was actually requested) or inventing a new native shim —
    /// this library does neither; it refuses loudly instead. A lone `Setsid` (no privilege drop) does not
    /// route through any such helper, so it composes with `Arg0` normally.
    member _.Arg0(arg0: string) =
        ArgumentNullException.ThrowIfNull arg0

        if arg0.Length = 0 then
            raise (ArgumentException("arg0 must not be empty", nameof arg0))

        CommandConfig.rejectEmbeddedNul (nameof arg0) arg0
        Command({ config with Arg0 = Some arg0 })

    /// Run the child with a **restricted token**: a copy of this process's own primary token created
    /// with `CreateRestrictedToken(DISABLE_MAX_PRIVILEGE)`, which strips every privilege the caller
    /// holds except the always-present `SeChangeNotifyPrivilege`. The child is then started with
    /// `CreateProcessAsUser` under that token. It keeps the caller's *identity* (same user, same SIDs,
    /// same file ACLs apply) but loses the ability to do the privileged things that identity could —
    /// debug another process, load a driver, take ownership, shut the machine down, impersonate.
    ///
    /// This is the Windows half of the hardening story whose Unix half is `Uid`/`Gid`/`Groups`; combine
    /// it with `WindowsIntegrityLevel` (which restricts what the child may *write to*, orthogonally to
    /// what it may *do*) and with the containing group's `ProcessGroupOptions` resource limits and
    /// `WindowsUiRestrictions` for the full perimeter — see the hardening guide.
    ///
    /// **Windows-only:** on POSIX a set value fails the spawn with `ProcessError.Unsupported`, never a
    /// silent no-op — the mirror image of `Uid`/`Setsid` failing on Windows. Rejected at the builder
    /// boundary in combination with `Pty` (a ConPTY run spawns through a different call that does not
    /// carry the token) and with the Unix-only `Uid`/`Gid`/`Groups`/`Umask`/`Setsid` family (a command
    /// carrying both halves could not run on *any* host). Elevation is unaffected: a restricted token
    /// cannot gain rights, only lose them, so this never turns into a privilege *escalation* path.
    member _.WindowsRestrictedToken() =
        CommandConfig.ensureWindowsTokenHardeningCompatible config "WindowsRestrictedToken"

        Command(
            { config with
                WindowsRestrictedToken = true }
        )

    /// Lower the child's **mandatory integrity level** to `level` (Windows), by labelling the token it
    /// is started with (`SetTokenInformation(TokenIntegrityLevel, ...)` on a duplicated primary token,
    /// spawned via `CreateProcessAsUser`). Windows' no-write-up policy then denies the child write
    /// access to anything labelled above that level — the user's own files, `HKCU`, and the windows of
    /// medium-integrity processes — regardless of the DACL that would otherwise allow it.
    ///
    /// Integrity is a *separate axis* from privileges: use `WindowsRestrictedToken` to take away what
    /// the child may **do**, and this to take away what it may **write to**. Both compose onto one
    /// token when set together. Only lowering is offered (`WindowsIntegrityLevel.Medium`/`Low`/
    /// `Untrusted`) — Windows refuses to raise a token's integrity, so a "higher" variant could only
    /// ever fail the spawn.
    ///
    /// The child's already-open handles are unaffected: the stdio pipes ProcessKit hands it were opened
    /// by the parent and their access check has already happened, so a `Low`-integrity child still
    /// writes its output back normally. **Windows-only**, with the same honest `ProcessError.Unsupported`
    /// on POSIX and the same builder-boundary conflicts as `WindowsRestrictedToken`.
    member _.WindowsIntegrityLevel(level: WindowsIntegrityLevel) =
        CommandConfig.ensureWindowsTokenHardeningCompatible config "WindowsIntegrityLevel"

        Command(
            { config with
                WindowsIntegrityLevel = Some level }
        )

    /// Opt in to reaping this child when the **parent process dies suddenly** — a SIGKILL, a crash, or a
    /// Windows `TerminateProcess` — the one case the deterministic kill-on-drop tree guarantee cannot
    /// cover, because it relies on a `Dispose`/`DisposeAsync` (or the finalizer) that a hard-killed parent
    /// never runs. Off by default; setting it changes nothing unless the parent actually dies unexpectedly.
    ///
    /// **What the platform actually guarantees differs — query it with `KillOnParentDeathScope`.**
    ///
    /// - **Windows — the whole tree, already, with no extra action.** Every child ProcessKit starts lives
    ///   in a Job Object created with `KILL_ON_JOB_CLOSE`, and the parent process owns the only handle to
    ///   that Job. When the parent dies for *any* reason the kernel closes its handles during process
    ///   rundown; closing the last Job handle terminates every process in the Job. So the guarantee holds
    ///   tree-wide and unconditionally — this method is a documented no-op on Windows, not a silent one.
    /// - **Linux — the direct child only.** The child is armed with `PR_SET_PDEATHSIG(SIGKILL)` via the
    ///   `setpriv --pdeathsig` helper (util-linux) on the ordinary `posix_spawn` path, so it is killed when
    ///   its parent dies. A parent that dies in the instant *before* that arming — where the signal would
    ///   otherwise bind to the reaper that adopted the orphan, and the child would run on — is covered too:
    ///   immediately after arming, and before the target program runs, the child checks (through `/bin/sh`,
    ///   pinned by absolute path) that its parent is still the exact process that spawned it, and
    ///   `SIGKILL`s itself instead of running the program when it is not. **Known limits (not silent):**
    ///   the parent-death signal is **not inherited** across
    ///   a `fork`, so a **grandchild** the child spawns is *not* covered — with the child's parent gone
    ///   nothing reaps its cgroup/pgroup. The kernel also **resets** `PR_SET_PDEATHSIG` when the child
    ///   `execve`s a **set-uid/set-gid** image, so for a `sudo`-like child the signal only holds up to that
    ///   `exec`. And because the signal is delivered when the **spawning thread** (not merely the process)
    ///   exits, and ProcessKit spawns on a thread-pool thread that .NET may retire while the process lives,
    ///   the reap is best-effort: it can fire early if that thread is reclaimed. The helper is loaded only
    ///   from a trusted system directory (`/usr/bin`, `/bin`, `/usr/sbin`, `/sbin`) and launched by
    ///   absolute path, never resolved on `PATH` — see `Uid` for why. Where no trusted directory holds
    ///   `setpriv` — a minimal image, or a non-FHS layout such as NixOS/Guix that keeps it only on the
    ///   `PATH` — the spawn fails with a typed `ProcessError.Spawn` naming the helper, never a silently
    ///   un-armed child. The `/bin/sh` that runs the parent check is a host requirement in the same way:
    ///   it is taken from that absolute path rather than `PATH`, and a host that has no shell there also
    ///   fails the spawn with a typed `ProcessError.Spawn` — never a child armed but left running with
    ///   the pre-arm window open.
    /// - **macOS/BSD — unsupported.** There is no `PR_SET_PDEATHSIG` analog, so a set value fails the spawn
    ///   with `ProcessError.Unsupported` rather than pretending the cleanup will happen.
    member _.KillOnParentDeath() =
        Command({ config with KillOnParentDeath = true })

    /// The **scope** of `KillOnParentDeath` cleanup the current platform actually guarantees —
    /// `WholeTree` (Windows Job Object), `DirectChildOnly` (Linux `PR_SET_PDEATHSIG`), or `Nothing`
    /// (macOS/BSD). Fixed per platform and **independent of whether `KillOnParentDeath()` was called**:
    /// this reports what the OS *can* do, the same honest-report principle as `ProcessGroup.Mechanism`.
    member _.KillOnParentDeathScope() : KillOnParentDeathScope = KillOnParentDeathScope.Current

    /// Run the child under an opt-in **pseudo-terminal (PTY)** with `pty`'s initial geometry and flags:
    /// the child gets a real controlling terminal (`isatty` true) on a **single merged stdout+stderr
    /// stream**, for tools that demand a tty — an interactive `ssh`/`sudo` password prompt, a credential
    /// helper, a TUI, or a progress bar that switches to "dumb" line-buffered output when it detects a
    /// pipe. A PTY is never implicit; the default (this method unset) is byte-identical to a plain pipe run.
    ///
    /// **One merged stream.** A tty is a single bidirectional device, so under a PTY the child's stdout
    /// and stderr are physically one stream: `ProcessResult.Stderr` is empty and `OutputEventsAsync` emits
    /// only `OutputEvent.Stdout` events. Because there is no separate stderr, the separate-stderr
    /// observation knobs are rejected at the builder boundary (`ArgumentException`, in either chaining
    /// order): `StderrTee` and `OnStderrLine`. `Setsid` is likewise rejected — it detaches the child into
    /// a new session with **no** controlling tty, contradicting a PTY's controlling pseudo-terminal.
    /// `InheritStdin` is rejected too (either chaining order): a PTY gives the child its own pty
    /// slave/ConPTY input as stdin, so there is no way to also hand it the parent's own standard input.
    /// Inside a `Pipeline` a PTY is allowed only as a standalone run or the **last** stage (its merged
    /// output would otherwise be injected into the downstream stage's stdin).
    ///
    /// **Platform support (typed, never a silent downgrade).** Windows: ConPTY, needing Windows 10 1809+;
    /// an older host fails the spawn with `ProcessError.Unsupported "Pty (needs Windows 10 1809+ /
    /// ConPTY)"`. POSIX: a real controlling pty via `openpty` + the `setsid --ctty` helper (util-linux),
    /// which — like the `setpriv` helper behind `Uid` — is loaded only from a trusted system directory
    /// (`/usr/bin`, `/bin`, `/usr/sbin`, `/sbin`) and launched by absolute path, never resolved on `PATH`.
    /// A host with `setsid` in none of those directories (a non-FHS layout such as NixOS/Guix, *even with
    /// `setsid` on its `PATH`*) or without the pty devfs (macOS/BSD) fails with
    /// `ProcessError.Unsupported`, never a socketpair silently pretending to be a tty.
    ///
    /// **Secret-safety.** A terminal echoes typed input into its captured output by default — see
    /// `PtyConfig` for the echo footgun and the `Echo` flag. argv/env values and any PTY credentials are
    /// never logged or traced.
    member _.Pty(pty: PtyConfig) =
        CommandConfig.validatePtyConfig pty
        CommandConfig.ensurePtyCompatible config
        CommandConfig.ensureNoFileRedirect config "Pty"
        Command({ config with Pty = Some pty })

    /// Run the child under a pseudo-terminal with the default 80×24 geometry (echo on). See
    /// `Command.Pty(PtyConfig)`.
    member this.Pty() = this.Pty PtyConfig.Default

    /// Run the child under a pseudo-terminal with the given initial geometry (echo on). `cols` and `rows`
    /// must each be at least 1 (rejected with `ArgumentOutOfRangeException`). See `Command.Pty(PtyConfig)`.
    member this.Pty(cols: int, rows: int) =
        this.Pty
            { PtyConfig.Default with
                Cols = cols
                Rows = rows }

    /// Emit structured lifecycle events (spawn / exit / timeout / retry) to `logger`. The program
    /// name and non-secret facts only — **argv and environment are never logged**.
    member _.Logger(logger: ILogger) =
        ArgumentNullException.ThrowIfNull logger
        Command({ config with Logger = Some logger })

    /// Stamp a per-run correlation id, shared by the run's log/trace events and its retries. Internal:
    /// the verb layer sets it once per logical run; a direct spawn falls back to a per-incarnation id.
    member internal _.WithRunId(runId: string) =
        Command({ config with RunId = Some runId })

    /// Carry a retrying run's hold on this command's one-shot stdin payload down to the launch
    /// boundary that will spawn each attempt. Internal: `Runner.withRetry` stamps it on the copy of the
    /// command it drives, so the attempt's `OneShotStdin.reserveLaunch` can take the loan on the run's
    /// own hold instead of refusing the attempt as a second consumer of a payload the run already owns.
    /// Never part of a caller's command — the reservation belongs to one run, not to the command value,
    /// which is why the loan (and not the stamp) is what a launch is actually allowed to spawn on.
    member internal _.WithStdinReservation(reservation: OneShotStdinReservation) =
        Command(
            { config with
                StdinReservation = Some reservation }
        )

    member internal _.WithRetryJitterSource(source: unit -> float) =
        ArgumentNullException.ThrowIfNull source

        Command(
            { config with
                RetryJitterSource = source }
        )

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

    /// Append a trusted Windows command-line fragment verbatim after every ordinary argument. Never
    /// place untrusted input in `fragment`; a POSIX spawn and an automatically resolved batch wrapper
    /// return typed `Unsupported`.
    let windowsRawArg (fragment: string) (command: Command) = command.WindowsRawArg fragment

    /// Set the working directory for the run.
    let currentDir (directory: string) (command: Command) = command.CurrentDir directory

    /// Add `directory` to the prefer-local search list, consulted before `PATH` when resolving the
    /// command's bare-name program (searched in the order added; a match is launched by its resolved
    /// absolute path). See `Command.PreferLocal`.
    let preferLocal (directory: string) (command: Command) = command.PreferLocal directory

    /// Set an environment variable for the child.
    let env (key: string) (value: string) (command: Command) = command.Env(key, value)

    /// Remove an inherited environment variable from the child.
    let envRemove (key: string) (command: Command) = command.EnvRemove key

    /// Start the child's environment empty instead of inheriting the parent's.
    let envClear (command: Command) = command.EnvClear()

    /// Feed the child's standard input from `source`. A one-shot source feeds at most one incarnation
    /// (see `Command.Stdin`).
    let stdin (source: Stdin) (command: Command) = command.Stdin source

    /// Hand the child the parent process's own standard input directly (inherited, no pipe/feeder), for
    /// interactive/console programs. See `Command.InheritStdin`.
    let inheritStdin (command: Command) = command.InheritStdin()

    /// Keep the child's stdin pipe open after the source is exhausted.
    let keepStdinOpen (command: Command) = command.KeepStdinOpen()

    /// Set how the child's standard output is connected.
    let stdout (mode: StdioMode) (command: Command) = command.Stdout mode

    /// Set how the child's standard error is connected.
    let stderr (mode: StdioMode) (command: Command) = command.Stderr mode

    /// Decode captured stdout with `encoding`.
    let stdoutEncoding (enc: Encoding) (command: Command) = command.StdoutEncoding enc

    /// Encode text sent to stdin with `encoding`.
    let stdinEncoding (enc: Encoding) (command: Command) = command.StdinEncoding enc

    /// Use `timeProvider` for retry delays, readiness probes, `PtySession` pattern deadlines, and supervision.
    let timeProvider (provider: TimeProvider) (command: Command) = command.TimeProvider provider

    /// Decode captured stderr with `encoding`.
    let stderrEncoding (enc: Encoding) (command: Command) = command.StderrEncoding enc

    /// Encode text stdin and decode both captured streams with `encoding`.
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

    /// Redirect the child's stdout straight to the file at `path` at the OS level (no parent pump — the
    /// file outlives the parent). `append` chooses create/truncate (`false`) or append (`true`). Leaves
    /// no parent-side stdout stream, so it is rejected with `StdoutTee`/`OnStdoutLine`/`MergeStderr`/`Pty`.
    /// See `Command.StdoutToFile`.
    let stdoutToFile (path: string) (append: bool) (command: Command) = command.StdoutToFile(path, append)

    /// Redirect the child's stderr straight to the file at `path` at the OS level — the stderr mirror of
    /// `stdoutToFile`. See `Command.StderrToFile`.
    let stderrToFile (path: string) (append: bool) (command: Command) = command.StderrToFile(path, append)

    /// Merge the child's stderr into its stdout at the OS level (like a shell `2>&1`); the two streams
    /// then interleave byte-for-byte on the single stdout stream, and there is no separate stderr stream.
    /// See `Command.MergeStderr`.
    let mergeStderr (command: Command) = command.MergeStderr()

    /// Bound the in-memory backlog of captured lines.
    let outputBuffer (policy: OutputBufferPolicy) (command: Command) = command.OutputBuffer policy

    /// Shape each decoded line as it enters the capture backlog (redaction at capture); handlers, tees,
    /// the streaming verbs and raw byte captures still see the unshaped line. See
    /// `Command.CapturePolicy`.
    let capturePolicy (policy: ICapturePolicy) (command: Command) = command.CapturePolicy policy

    /// Opt in to a bounded/backpressure channel for the streaming verbs (default stays unbounded).
    let streamBuffer (policy: StreamBufferPolicy) (command: Command) = command.StreamBuffer policy

    /// Kill the run `duration` after it was spawned.
    let timeout (duration: TimeSpan) (command: Command) = command.Timeout duration

    /// Terminate gracefully on timeout, force-killing only after `grace`.
    let timeoutGrace (grace: TimeSpan) (command: Command) = command.TimeoutGrace grace

    /// Choose the soft signal used by graceful stop paths before hard-kill escalation.
    let stopSignal (signal: Signal) (command: Command) = command.StopSignal signal

    /// Kill the run when it produces no output (stdout or stderr) for `duration` — reset by each chunk
    /// of output — independent of the total `Command.Timeout`.
    let idleTimeout (duration: TimeSpan) (command: Command) = command.IdleTimeout duration

    /// Also cancel the run when `cancellationToken` fires.
    let cancelOn (cancellationToken: CancellationToken) (command: Command) = command.CancelOn cancellationToken

    /// Tear a CANCELLED run down gracefully: soft signal, up to `grace` to leave, then the hard kill.
    /// Independent of `timeoutGrace`; the outcome stays `ProcessError.Cancelled` either way.
    let cancelGrace (grace: TimeSpan) (command: Command) = command.CancelGrace grace

    /// Choose the soft signal that opens a `cancelGrace` window (default `Signal.Term`). Inert without
    /// `cancelGrace`, and independent of `stopSignal`.
    let cancelSignal (signal: Signal) (command: Command) = command.CancelSignal signal

    /// Run the command up to `maxAttempts` times in total (initial run plus retries), waiting `delay`
    /// between attempts (`0`/`1` both mean a single run). A negative `delay` is rejected; delays
    /// beyond the maximum armable timer interval are clamped when the retry runs. If the predicate
    /// throws, the consuming verb returns `ProcessError.RetryPredicate` with the original attempt
    /// error in `Original`, and no further attempt runs.
    let retry (maxAttempts: int) (delay: TimeSpan) (shouldRetry: ProcessError -> bool) (command: Command) =
        command.Retry(maxAttempts, delay, Func<ProcessError, bool> shouldRetry)

    /// Run the command with exponential retry backoff: `baseDelay × factor^n`, capped at `maxDelay`
    /// before optional jitter. Retry timers use the command's `TimeProvider`. If the predicate throws,
    /// the consuming verb returns `ProcessError.RetryPredicate` with the original attempt error in
    /// `Original`, and no further attempt runs.
    let retryBackoff
        (maxAttempts: int)
        (baseDelay: TimeSpan)
        (factor: float)
        (maxDelay: TimeSpan)
        (jitter: bool)
        (shouldRetry: ProcessError -> bool)
        (command: Command)
        =
        command.RetryBackoff(maxAttempts, baseDelay, factor, maxDelay, jitter, Func<ProcessError, bool> shouldRetry)

    /// Explicitly disable retrying for this command, overriding any inherited `Retry` policy (e.g.
    /// from a `CliClient.WithDefaults` template). The command always runs exactly once.
    let retryNever (command: Command) = command.RetryNever()

    /// Add a POSIX full-duplex parent/child channel at child fd `targetFd` (3 or greater). Claim the
    /// parent stream once with `RunningProcess.TakeExtraFd(targetFd)` after starting the command.
    let extraFd (targetFd: int) (command: Command) = command.ExtraFd targetFd

    /// Inside a pipeline, allow this stage to exit non-zero without failing the pipeline.
    let uncheckedInPipe (command: Command) = command.UncheckedInPipe()

    /// Replace the success exit-code set with these codes (default `{0}`; include `0` to keep it). An
    /// empty set is rejected at the builder boundary with `ArgumentException`. See `Command.OkCodes`.
    let okCodes (codes: seq<int>) (command: Command) = command.OkCodes codes

    /// Windows: run the child with `CREATE_NO_WINDOW` (no effect on Unix).
    let createNoWindow (command: Command) = command.CreateNoWindow()

    /// Windows: register a ConPTY child as the target for best-effort CTRL+BREAK through
    /// `ProcessGroup.Signal(Signal.Int/Term)`; ConPTY children already have process-group isolation.
    /// Regular children are put in a new process group. No effect on Unix. See `Command.WindowsCtrlSignals`.
    let windowsCtrlSignals (command: Command) = command.WindowsCtrlSignals()

    /// Launch the child (and its spawned tree) at a lower/higher CPU-scheduling priority (Windows
    /// priority class / Unix nice). Supported on both platforms; the default leaves the OS default.
    let priority (level: Priority) (command: Command) = command.Priority level

    /// Set the child's **Linux I/O-scheduling** priority (`ioprio_set(2)`) — the block-device axis, not
    /// the CPU one `priority` sets. Build the value with `IoPriority.Idle`/`IoPriority.BestEffort`/
    /// `IoPriority.RealTime`. In force from the child's first disk request and inherited by its
    /// descendants. Linux-only: a set value fails a Windows/macOS/BSD spawn — and a detached launch —
    /// with `ProcessError.Unsupported`. See `Command.IoPriority`.
    let ioPriority (level: IoPriority) (command: Command) = command.IoPriority level

    /// Set the child's Unix file-mode creation mask (`umask(2)`). Unix-only: a set mask fails a Windows
    /// spawn with `ProcessError.Unsupported`. The default leaves the inherited umask untouched.
    let umask (mask: int) (command: Command) = command.Umask mask

    /// Cap one Unix per-process resource for the child (`setrlimit(2)`): `soft` is the value in force,
    /// `hard` the ceiling the child may raise it back to, both in the resource's native unit (bytes,
    /// seconds, or a count). Different resources accumulate; the same one replaces in place. Unix-only:
    /// a set limit fails a Windows spawn with `ProcessError.Unsupported`, and a POSIX host without the
    /// util-linux `prlimit` helper fails with `ProcessError.ResourceLimit`. Combined with a whole-tree
    /// `ResourceLimits.CpuTimeMax`, the stricter CPU-time value wins. See `Command.Rlimit`.
    let rlimit (resource: RlimitResource) (soft: int64) (hard: int64) (command: Command) =
        command.Rlimit(resource, soft, hard)

    /// Run the child under this Unix user id (`setuid`). Unix-only: a set uid fails a Windows spawn with
    /// `ProcessError.Unsupported`; dropping needs privilege (else `ProcessError.Spawn`). See `Command.Uid`.
    let uid (value: int) (command: Command) = command.Uid value

    /// Run the child under this Unix group id (`setgid`). Unix-only, same notes as `uid`. See `Command.Gid`.
    let gid (value: int) (command: Command) = command.Gid value

    /// Run the child under this Unix user and group id (the privilege-drop pair). See `Command.User`.
    let user (uid: int) (gid: int) (command: Command) = command.User(uid, gid)

    /// Set the child's Unix supplementary groups, replacing the inherited set — the third leg of a
    /// privilege drop. Meaningful only alongside a `Uid`/`Gid` drop (else `ProcessError.Spawn`);
    /// Unix-only (a set value fails a Windows spawn with `ProcessError.Unsupported`). See `Command.Groups`.
    let groups (gids: seq<int>) (command: Command) = command.Groups gids

    /// Detach the child into a new session (`setsid()`). Unix-only: a set request fails a Windows spawn
    /// with `ProcessError.Unsupported`. Containment is preserved. See `Command.Setsid`.
    let setsid (command: Command) = command.Setsid()

    /// Override the child's `argv[0]` independently of `Program` (multicall binaries, login-shell
    /// conventions). Unix-only: a set value fails a Windows spawn with `ProcessError.Unsupported`, as
    /// does combining it with a `Uid`/`Gid`/`Groups`/`KillOnParentDeath` drop, `Pty`, or a cgroup-backend
    /// run — none of their re-`exec`ing helpers has a seam for a distinct `argv[0]`. See `Command.Arg0`.
    let arg0 (value: string) (command: Command) = command.Arg0 value

    /// Run the child with a restricted token (`CreateRestrictedToken` + `DISABLE_MAX_PRIVILEGE`), keeping
    /// the caller's identity but none of its privileges. Windows-only: a set request fails a POSIX spawn
    /// with `ProcessError.Unsupported`. See `Command.WindowsRestrictedToken`.
    let windowsRestrictedToken (command: Command) = command.WindowsRestrictedToken()

    /// Lower the child's Windows mandatory integrity level, denying it write access to anything labelled
    /// above that level. Windows-only: a set request fails a POSIX spawn with `ProcessError.Unsupported`.
    /// See `Command.WindowsIntegrityLevel`.
    let windowsIntegrityLevel (level: WindowsIntegrityLevel) (command: Command) = command.WindowsIntegrityLevel level

    /// Opt in to reaping this child when the parent process dies suddenly (SIGKILL/crash/`TerminateProcess`).
    /// Windows reaps the whole Job tree with no extra action; Linux the direct child only via
    /// `PR_SET_PDEATHSIG` (`setpriv --pdeathsig`); macOS/BSD fail the spawn with `ProcessError.Unsupported`.
    /// Query the platform-fixed scope with `Command.KillOnParentDeathScope`. See `Command.KillOnParentDeath`.
    let killOnParentDeath (command: Command) = command.KillOnParentDeath()

    /// The platform-fixed scope of `KillOnParentDeath` cleanup — `WholeTree` (Windows), `DirectChildOnly`
    /// (Linux), or `Nothing` (macOS/BSD) — independent of whether the verb was set. See
    /// `Command.KillOnParentDeathScope`.
    let killOnParentDeathScope (command: Command) = command.KillOnParentDeathScope()

    /// Run the child under a pseudo-terminal (PTY) with the default 80×24 geometry (echo on) — a single
    /// merged stdout+stderr terminal stream, for tools that demand a tty. Windows: ConPTY (Win10 1809+);
    /// POSIX: a real controlling pty via `openpty` + the `setsid --ctty` helper (util-linux); a host
    /// missing that ctty helper or the pty devfs (macOS/BSD) fails with `ProcessError.Unsupported`. See
    /// `Command.Pty`.
    let pty (command: Command) = command.Pty()

    /// Run the child under a pseudo-terminal (PTY) with a full `PtyConfig` (geometry, echo). See
    /// `Command.Pty(PtyConfig)`.
    let ptyConfig (pty: PtyConfig) (command: Command) = command.Pty pty

    /// Run the child under a pseudo-terminal (PTY) with the given initial geometry (echo on). See
    /// `Command.Pty(cols, rows)`.
    let ptySize (cols: int) (rows: int) (command: Command) = command.Pty(cols, rows)

    /// Emit structured lifecycle events to `logger` (argv/env never logged).
    let logger (logger: ILogger) (command: Command) = command.Logger logger
