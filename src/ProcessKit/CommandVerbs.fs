namespace ProcessKit

open System
open System.Diagnostics.CodeAnalysis
open System.Runtime.CompilerServices
open System.Runtime.InteropServices
open System.Text.Json
open System.Text.Json.Serialization.Metadata
open System.Threading

/// The configuration guards for the detached launch (`Command.LaunchDetached`). A detached child has no
/// parent-side machinery at all — no pump, no watchdog, no containment, no exit observation — so every
/// builder knob that would need one is refused HERE, before anything is spawned, with a typed
/// `ProcessError.Unsupported` naming the knob and why it cannot be honoured. That is the whole design
/// stance of the verb: the incompatible knobs fail loudly rather than being quietly ignored, so a
/// `Timeout` can never look applied when nothing will ever enforce it.
///
/// `Unsupported` (not `Spawn`) is the right case for all of them, matching how the library already
/// reports a fixed capability gap "on this platform or in this configuration" (a Unix-only knob on
/// Windows, `CurrentDir` on an old libc) as opposed to a per-invocation launch failure.
module internal DetachedLaunch =

    let private refuse (knob: string) (reason: string) =
        ProcessError.Unsupported
            $"{knob} on a detached launch: {reason}. Use StartAsync/RunAsync instead, or drop {knob}."

    /// The first knob on `command` that a detached launch cannot honour, or `None` when the command is
    /// launchable as-is. Checked in a fixed order so the same configuration always names the same knob.
    ///
    /// Deliberately NOT refused, because a detached child can honour them at the OS level with no parent
    /// involvement: `CurrentDir`, `Env`/`EnvClear`/`PreferLocal`, `StdoutToFile`/`StderrToFile`,
    /// `MergeStderr`, `Stdout`/`Stderr` `Null`/`Inherit`, `InheritStdin`, `CreateNoWindow`,
    /// `WindowsCtrlSignals`, `WindowsRestrictedToken`, `WindowsIntegrityLevel`, `Priority`, `Umask`,
    /// `Uid`/`Gid`/`Groups`, `Setsid` (which a POSIX detached launch performs anyway), and `Arg0` (applied
    /// directly on the POSIX detached spawn path; refused there only if paired with a `Uid`/`Gid`/`Groups`
    /// drop, exactly as on the contained path — see `Native.Posix.arg0HelperConflict`). The Windows
    /// hardening knobs are honoured by the Windows detached spawn and fail with the same typed
    /// `ProcessError.Unsupported` as every Windows-only hardening request on POSIX. `StdioMode.Piped` —
    /// the default — is wired to the null device
    /// rather than refused: there is no parent left to drain a pipe, and refusing the default mode would
    /// make the verb unusable without three extra builder calls. Knobs that only a capture/exit-observing
    /// verb ever reads (`StdoutEncoding`/`StderrEncoding`, the line terminators, `OutputBuffer`,
    /// `OkCodes`, `UncheckedInPipe`) are no-ops here exactly as they are on the verbs that ignore them
    /// today; they are documented as such rather than refused, since a default-valued knob cannot be told
    /// apart from an explicitly set one.
    let incompatibleKnob (command: Command) : ProcessError option =
        let config = command.Config

        if config.ExtraFds.Count > 0 then
            Some(
                refuse
                    "ExtraFd"
                    "an extra descriptor is a live parent-side channel, and a detached launch returns no RunningProcess that could own or expose it"
            )
        elif config.Pty.IsSome then
            Some(
                refuse
                    "Pty"
                    "a pseudo-terminal is a live parent-side device (the ConPTY handle / pty master fd) that has to be owned, pumped and closed by this process, which a detached child by definition has no one to do"
            )
        elif config.KillOnParentDeath then
            Some(
                refuse
                    "KillOnParentDeath"
                    "it asks the OS to kill the child when this process dies, the exact opposite of detaching a child so it survives us"
            )
        elif config.Timeout.IsSome then
            Some(refuse "Timeout" "enforcing a deadline needs a parent-side watchdog that can still kill the child")
        elif config.TimeoutGrace.IsSome then
            Some(refuse "TimeoutGrace" "it only softens a Timeout kill, and there is no timeout to enforce")
        elif config.IdleTimeout.IsSome then
            Some(
                refuse
                    "IdleTimeout"
                    "watching for silence needs the parent to be reading the child's output, and to be able to kill it"
            )
        elif config.CancelOn.IsSome then
            Some(
                refuse
                    "CancelOn"
                    "cancelling a run means killing the child, which is precisely the control a detached launch gives up"
            )
        elif config.StdinSource.IsSome && not (Stdin.isInherit config.StdinSource) then
            Some(
                refuse
                    "Stdin"
                    "feeding stdin needs a parent-side pump writing into the child's pipe; InheritStdin (the parent's own standard input) is supported, and any other source is not"
            )
        elif config.KeepStdinOpen then
            Some(
                refuse
                    "KeepStdinOpen"
                    "it retains the parent's end of the stdin pipe for interactive writing, and the descriptor a detached launch returns has no stdin to write to"
            )
        elif config.OnStdoutLine.IsSome then
            Some(refuse "OnStdoutLine" "per-line handlers need the parent to be pumping the child's stdout")
        elif config.OnStderrLine.IsSome then
            Some(refuse "OnStderrLine" "per-line handlers need the parent to be pumping the child's stderr")
        elif config.StdoutTee.IsSome then
            Some(
                refuse
                    "StdoutTee"
                    "a tee is fed by the parent's own copy of the child's stdout; redirect the child's stdout straight to a file with StdoutToFile instead"
            )
        elif config.StderrTee.IsSome then
            Some(
                refuse
                    "StderrTee"
                    "a tee is fed by the parent's own copy of the child's stderr; redirect the child's stderr straight to a file with StderrToFile instead"
            )
        elif config.StreamBuffer.IsSome then
            Some(refuse "StreamBuffer" "it bounds a streaming backlog, and nothing streams a detached child's output")
        elif config.Retry.IsSome && not config.RetryDisabled then
            Some(
                refuse
                    "Retry"
                    "retrying is a verb-layer policy over an observed failure, and a detached launch performs the spawn exactly once (RetryNever opts a command inheriting a client default back out)"
            )
        else
            None

/// Default-runner convenience verbs on `Command`, callable from F# and C# as
/// `command.StartAsync()` / `command.RunAsync()` etc. They use a shared `JobRunner`; for a custom or
/// injected runner, go through `Runner.*` or call the runner directly. The `cancellationToken` is
/// optional and defaults to `CancellationToken.None`.
[<Extension>]
type CommandVerbs =

    static member val internal DefaultRunner: IProcessRunner = JobRunner()

    /// Start the command and return a live `RunningProcess`.
    [<Extension>]
    static member StartAsync(command: Command, [<Optional>] cancellationToken: CancellationToken) =
        ArgumentNullException.ThrowIfNull command
        Runner.start CommandVerbs.DefaultRunner cancellationToken command

    /// Require a zero/accepted exit and return stdout, trailing whitespace trimmed. Output the
    /// command's `OutputBuffer` policy truncated is refused with `ProcessError.OutputTooLarge`, and
    /// output the bounded post-exit drain cut short with `ProcessError.OutputIncomplete`, rather
    /// than returned as if whole — use `OutputStringAsync` for the bounded payload plus `Truncated`.
    [<Extension>]
    static member RunAsync(command: Command, [<Optional>] cancellationToken: CancellationToken) =
        ArgumentNullException.ThrowIfNull command
        Runner.run CommandVerbs.DefaultRunner cancellationToken command

    /// Require a zero/accepted exit, discarding the captured output — including when the buffer
    /// policy truncated it, which this verb makes no claim about.
    [<Extension>]
    static member RunUnitAsync(command: Command, [<Optional>] cancellationToken: CancellationToken) =
        ArgumentNullException.ThrowIfNull command
        Runner.runUnit CommandVerbs.DefaultRunner cancellationToken command

    /// Run to completion, capturing stdout as decoded text (a non-zero exit is data).
    [<Extension>]
    static member OutputStringAsync(command: Command, [<Optional>] cancellationToken: CancellationToken) =
        ArgumentNullException.ThrowIfNull command
        Runner.outputString CommandVerbs.DefaultRunner cancellationToken command

    /// Run to completion, capturing stdout as raw bytes.
    [<Extension>]
    static member OutputBytesAsync(command: Command, [<Optional>] cancellationToken: CancellationToken) =
        ArgumentNullException.ThrowIfNull command
        Runner.outputBytes CommandVerbs.DefaultRunner cancellationToken command

    /// The exit code; a signal kill or timeout errors instead of inventing a sentinel code.
    [<Extension>]
    static member ExitCodeAsync(command: Command, [<Optional>] cancellationToken: CancellationToken) =
        ArgumentNullException.ThrowIfNull command
        Runner.exitCode CommandVerbs.DefaultRunner cancellationToken command

    /// Read the exit code as a yes/no answer: 0 -> true, 1 -> false, anything else errors.
    [<Extension>]
    static member ProbeAsync(command: Command, [<Optional>] cancellationToken: CancellationToken) =
        ArgumentNullException.ThrowIfNull command
        Runner.probe CommandVerbs.DefaultRunner cancellationToken command

    /// Require a zero/accepted exit and parse the trimmed stdout into a `'T`; a thrown parser error
    /// becomes `ProcessError.Parse`.
    [<Extension>]
    static member ParseAsync
        (command: Command, parser: Func<string, 'T>, [<Optional>] cancellationToken: CancellationToken)
        =
        ArgumentNullException.ThrowIfNull command
        ArgumentNullException.ThrowIfNull parser
        Runner.parse CommandVerbs.DefaultRunner cancellationToken parser.Invoke command

    /// Like `ParseAsync`, but with the standard .NET try-parse shape: pass a BCL parser like
    /// `int.TryParse` with an explicit type argument (`TryParseAsync&lt;int&gt;(int.TryParse)` — needed
    /// because BCL `TryParse` is overloaded). A `false` return becomes `ProcessError.Parse`.
    /// (F# can use the `Result`-returning `Runner.tryParse`.)
    [<Extension>]
    static member TryParseAsync
        (command: Command, parser: TryParser<'T>, [<Optional>] cancellationToken: CancellationToken)
        =
        ArgumentNullException.ThrowIfNull command
        ArgumentNullException.ThrowIfNull parser
        Runner.tryParse CommandVerbs.DefaultRunner cancellationToken (TryParser.toResult parser) command

    /// Require a zero/accepted exit and deserialize the trimmed stdout as JSON into a `'T` via
    /// `System.Text.Json` (`options` omitted uses the BCL defaults); invalid JSON becomes
    /// `ProcessError.Parse`, just like `ParseAsync`. Give an explicit type argument, e.g.
    /// `cmd.OutputJsonAsync&lt;MyRecord&gt;()` — there is no parser argument to infer `'T` from.
    ///
    /// **Trimming / AOT:** deserializes via reflection-based `System.Text.Json`
    /// (`JsonSerializer.Deserialize(string, Type, JsonSerializerOptions)`), so it is not trim-/AOT-safe —
    /// pass `options` with a source-generated `JsonSerializerContext`/`JsonTypeInfo&lt;'T&gt;` resolver, or
    /// avoid this verb, in a trimmed/NativeAOT app.
    [<Extension>]
    [<RequiresUnreferencedCode "Deserializes stdout by reflection via System.Text.Json; give options a source-generated JsonSerializerContext, or avoid this verb, in a trimmed app.">]
    [<RequiresDynamicCode "Deserializes stdout by reflection via System.Text.Json; give options a source-generated JsonSerializerContext, or avoid this verb, in a NativeAOT app.">]
    static member OutputJsonAsync<'T>
        (
            command: Command,
            [<Optional>] options: JsonSerializerOptions | null,
            [<Optional>] cancellationToken: CancellationToken
        ) =
        ArgumentNullException.ThrowIfNull command
        Runner.outputJson<'T> CommandVerbs.DefaultRunner cancellationToken (Option.ofObj options) command

    /// Require a zero/accepted exit and deserialize the trimmed stdout using source-generated
    /// `JsonTypeInfo<'T>` metadata. Invalid JSON becomes `ProcessError.Parse`; unlike the
    /// `JsonSerializerOptions` overload, this overload is safe for trimmed and NativeAOT applications.
    [<Extension>]
    static member OutputJsonAsync<'T>
        (command: Command, typeInfo: JsonTypeInfo<'T>, [<Optional>] cancellationToken: CancellationToken)
        =
        ArgumentNullException.ThrowIfNull command
        ArgumentNullException.ThrowIfNull typeInfo
        Runner.outputJsonTyped<'T> CommandVerbs.DefaultRunner cancellationToken typeInfo command

    /// The first stdout line satisfying `predicate`, or `None` if stdout closes without a match.
    [<Extension>]
    static member FirstLineAsync
        (command: Command, predicate: Func<string, bool>, [<Optional>] cancellationToken: CancellationToken)
        =
        ArgumentNullException.ThrowIfNull command
        ArgumentNullException.ThrowIfNull predicate
        Runner.firstLine CommandVerbs.DefaultRunner cancellationToken predicate.Invoke command

    /// Encode this command's text stdin and decode its captured stdout **and** stderr with the local console
    /// encoding instead of UTF-8 — the one-line fix for a legacy Windows console program whose non-ASCII
    /// input/output otherwise becomes mojibake. Equivalent to
    /// `Encoding(ConsoleEncoding.current ())`, which documents exactly what is resolved: this process's
    /// console output code page (or the system OEM code page when it has no console) on Windows, and
    /// UTF-8 — the unchanged default, no P/Invoke, nothing to undo — everywhere else.
    ///
    /// **Opt-in, and the default is untouched.** Without this call captured text is still decoded UTF-8
    /// on every platform, which is correct for every modern tool; reach for it for the pre-UTF-8
    /// programs (`ping`, `netstat`, `chkdsk`, an old in-house CLI) whose output comes back mangled. It
    /// is an ordinary builder knob, so `StdoutEncoding`/`StderrEncoding`/`Encoding` later in the same
    /// chain override it (and it overrides them) — the last one wins, as everywhere else.
    ///
    /// **Resolved here, once.** The code page is read as THIS call runs and the resulting `Encoding` is
    /// stored in the returned command; a `Command` is immutable, so nothing re-reads it at spawn time or
    /// while the child runs. A `chcp` issued after the command was built is picked up only by a command
    /// built again — or given `Encoding(ConsoleEncoding.current ())` before the launch — which is worth
    /// knowing for a command built once and reused (a long-lived `CliClient`, a template in a field).
    ///
    /// The captured *bytes* are never affected: `OutputBytesAsync` and the raw tees stay byte-exact
    /// regardless of which encoding decodes the text.
    ///
    /// (An extension member rather than a `Command` method only because of F# compile order: it reads
    /// the console code page through the native layer, which compiles after `Command.fs`.)
    [<Extension>]
    static member ConsoleEncoding(command: Command) : Command =
        ArgumentNullException.ThrowIfNull command
        command.Encoding(ConsoleEncoding.current ())

    /// Resolve this command's program to a full path WITHOUT spawning it — a preflight/`doctor` check
    /// ("will this command find its program?"), synchronous and side-effect-free (a few `stat`s, no
    /// process), unlike probing availability by actually launching it (`ProbeAsync`). Resolution is
    /// against the **effective child** `PATH`: the command's own `Env`/`EnvRemove`/`EnvClear` (a `PATH`
    /// override) applied to the inherited environment, with its `PreferLocal` directories consulted first
    /// — exactly the `PATH`/PATHEXT/executable-bit resolution the real spawn goes through (one shared
    /// resolver, no second copy). On success it returns the resolved absolute path; on a miss it returns
    /// the SAME typed `ProcessError.NotFound` — with the SAME `Searched` diagnostic — a real spawn of this
    /// command would fail with. A relative path-form program (`./tool`, `bin/tool`) is resolved against
    /// `CurrentDir` when configured, matching the child's launch directory on every platform.
    ///
    /// **Differs from `Exec.which`.** `Exec.which` (and `CliClient.EnsureAvailableAsync`) resolves against
    /// the CURRENT PROCESS's `PATH`, with no prefer-local — "is this tool installed on the host". This
    /// resolves against THIS command's effective environment and prefer-local — "will this command, as
    /// configured, find its program". Use `which` for a host-wide install check; use `ResolveProgram` when
    /// the command overrides `PATH` (`Env`) or leans on `PreferLocal`.
    [<Extension>]
    static member ResolveProgram(command: Command) : Result<string, ProcessError> =
        ArgumentNullException.ThrowIfNull command
        Native.Common.resolveCommandProgram command

    /// Launch this command **outside all containment** and let it go — the library's single, deliberate
    /// opt-out from the whole-tree kill-on-dispose guarantee, for the cases containment makes impossible:
    /// a self-updater that must outlive the process it replaces, a restart-myself relaunch, a daemon or
    /// agent handed off to the OS. On success it returns a `DetachedProcess` — a pid + start-time
    /// identity snapshot, nothing more.
    ///
    /// **What you give up.** There is no `RunningProcess`, no `ProcessGroup`, no `Outcome`, and nothing
    /// to dispose: the child is placed in **no Job Object** (Windows) and in **its own session**
    /// (`setsid`, POSIX), no handle to it is retained, and no exit is ever observed. Nothing this process
    /// does — `Dispose`, GC, even dying — will reach it. That is the entire point; if you want a
    /// deadline, output, an exit code, or a kill, use `StartAsync`/`RunAsync` (or a `ProcessGroup`)
    /// instead. `ProcessGroup`-level knobs (`ResourceLimits`, `ProcessGroupOptions`) are not merely
    /// ignored here but unreachable: they live on the container this verb refuses to create.
    ///
    /// **Every incompatible builder knob is refused, not ignored** — `Pty`, `KillOnParentDeath`,
    /// `Timeout`/`TimeoutGrace`/`IdleTimeout`, `CancelOn`, a feeder `Stdin` source, `KeepStdinOpen`, the
    /// line handlers and tees, `StreamBuffer`, and an active `Retry` policy each come back as a typed
    /// `ProcessError.Unsupported` naming the knob, before anything is spawned. `StdioMode.Piped` (the
    /// default) is the one deliberate exception: with no parent left to drain a pipe it is wired to the
    /// null device, so keep output with `StdoutToFile`/`StderrToFile`, or share the caller's own console
    /// with `Stdout(StdioMode.Inherit)`.
    ///
    /// **Synchronous by design.** Like `ProcessGroup.Create`/`Adopt` and `ResolveProgram`, this does one
    /// bounded OS call and has nothing to await — there is no run to wait for — so it returns the
    /// `Result` directly rather than a `Task` that never yields.
    ///
    /// **Platform notes.** POSIX: the child gets a new session (no controlling terminal), so a terminal
    /// hangup cannot reach it; because `posix_spawn` cannot reparent, it stays this process's direct
    /// child while the parent lives, so if it exits *first* a private reaper consumes that leader's wait
    /// status and a long-lived host does not accumulate zombies (if the parent exits first, the OS
    /// reparents the child and its new supervisor owns reaping). Windows: the child shares the
    /// caller's console unless you add `CreateNoWindow()` (or `WindowsCtrlSignals()`, which puts it in
    /// its own console process group), so a console-close event still reaches it in the default wiring.
    /// `WindowsRestrictedToken()` and `WindowsIntegrityLevel(...)` remain effective on Windows: detaching
    /// opts out of containment, not the requested token hardening. On POSIX both remain honestly
    /// unsupported, returning the usual typed `ProcessError.Unsupported` before launch.
    /// On both platforms this opts out of the containment ProcessKit creates, not one THIS process was
    /// itself placed in: a child of a job-bound Windows process joins that job by kernel rule, and a
    /// Linux child inherits this process's cgroup (so a `systemctl stop` of the unit still reaps it).
    ///
    /// The launch deliberately bypasses the `IProcessRunner` seam — it is an opt-out from running under
    /// ProcessKit, not a run — so a test double (`ScriptedRunner`, `RecordReplayRunner`) does not
    /// intercept it; put your own seam in front of it if a test must avoid launching anything.
    [<Extension>]
    static member LaunchDetached(command: Command) : Result<DetachedProcess, ProcessError> =
        ArgumentNullException.ThrowIfNull command

        match DetachedLaunch.incompatibleKnob command with
        | Some error -> Error error
        | None ->
            let spawned =
                if RuntimeInformation.IsOSPlatform OSPlatform.Windows then
                    Native.Windows.spawnDetachedWindows command
                else
                    Native.Posix.spawnDetachedPosix command

            match spawned with
            | Error error -> Error error
            | Ok detached ->
                // One lifecycle line for an operator: a detached launch is precisely the event worth
                // seeing in a log, since nothing downstream will ever report this child's exit. Reuses
                // the ordinary `ProcessSpawned` event id (no new taxonomy) with a fresh run id, since a
                // detached launch has no run to correlate with. Argv and environment are never logged.
                let runId = command.Config.RunId |> Option.defaultWith Diag.newRunId
                Log.spawn command.Config.Logger command.Program (Some detached.Pid) runId
                Ok(DetachedProcess(detached.Pid, command.Program, detached.StartTime))
