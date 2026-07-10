namespace ProcessKit

open System
open Microsoft.Extensions.Logging

/// Internal structured logging for the process lifecycle. Every event is a no-op when no `ILogger`
/// is configured (the common case), so logging is opt-in and free otherwise. Each event has a stable
/// `EventId` (name + number) so a consumer can filter/route by id, and every message is emitted through
/// a cached `LoggerMessage.Define` delegate — no format/boxing when the level is disabled.
///
/// A per-run `RunId` (generated at spawn, shared across a run's retries) rides on every run-scoped event
/// (spawn/exit/timeout/retry), so a run's lines tie together across a concurrent fleet even in a sink
/// that does not capture scopes.
///
/// **Security: argv and environment are never logged.** A command line carries secrets (a
/// `--password=…` flag, a token); the environment carries credentials. Only the program *name* and
/// non-secret lifecycle facts (pid, outcome, durations, retry/restart counts, run id) are emitted.
module internal Log =

    // The stable EventIds are the public `ProcessKitDiagnostics.Events`, so a consumer filters/routes by
    // the same ids the library tags with.
    module Events = ProcessKitDiagnostics.Events

    // Cached message delegates (allocation-free; skip formatting/boxing when the level is disabled).
    let private spawnMessage =
        LoggerMessage.Define<string, string, string>(
            LogLevel.Debug,
            Events.ProcessSpawned,
            "processkit: spawned {Program} (pid {Pid}, run {RunId})"
        )

    let private exitMessage =
        LoggerMessage.Define<string, string, double, string>(
            LogLevel.Debug,
            Events.ProcessExited,
            "processkit: {Program} finished ({Outcome}) in {DurationMs}ms (run {RunId})"
        )

    let private timeoutMessage =
        LoggerMessage.Define<string, double, string>(
            LogLevel.Warning,
            Events.ProcessTimedOut,
            "processkit: {Program} timed out after {TimeoutMs}ms (run {RunId})"
        )

    // Idle-timeout shares the `ProcessTimedOut` EventId (a consumer routing by id catches both kinds of
    // deadline kill), but the human-readable message names it "idle" and reports the idle window rather
    // than the total timeout, so a log reader can tell a no-output hang from a total-length overrun.
    let private idleTimeoutMessage =
        LoggerMessage.Define<string, double, string>(
            LogLevel.Warning,
            Events.ProcessTimedOut,
            "processkit: {Program} idle-timed out after {IdleMs}ms with no output (run {RunId})"
        )

    let private retryMessage =
        LoggerMessage.Define<string, int, double, string>(
            LogLevel.Debug,
            Events.ProcessRetry,
            "processkit: retrying {Program} (attempt {Attempt}) after {DelayMs}ms (run {RunId})"
        )

    let private supervisorRestartMessage =
        LoggerMessage.Define<string, int, double>(
            LogLevel.Debug,
            Events.SupervisorRestart,
            "processkit: supervisor restarting {Program} (restart {Restart}) after {DelayMs}ms"
        )

    let private stormPauseMessage =
        LoggerMessage.Define<string, double>(
            LogLevel.Warning,
            Events.SupervisorStormPause,
            "processkit: supervisor failure storm for {Program} — pausing restarts for {PauseMs}ms"
        )

    // Every lifecycle event runs the consumer's `ILogger` synchronously (its `IsEnabled`/`Log`), so a
    // faulty sink must never derail the process operation that emitted the event — logging is strictly
    // observational. `emitSafely` is the single seam every event flows through: it short-circuits when no
    // logger is configured (the common case, free) and swallows any fault the sink raises, so no call
    // site (spawn/exit/timeout/retry/supervisor/pipeline) can be broken by a broken logger. A raised
    // fault is deliberately NOT re-reported through the same (faulting) logger — that risks recursion — so
    // it is dropped rather than routed back into the sink that raised it. Not on any hot path: these fire
    // once per lifecycle event, never per output line, so the guarded closure costs nothing that matters.
    let private emitSafely (logger: ILogger option) (emit: ILogger -> unit) =
        match logger with
        | Some log ->
            try
                emit log
            with _ ->
                // The consumer's ILogger threw; logging is observational, so the process operation must
                // proceed unaffected. Not re-logged through the same (faulting) logger, to avoid recursion.
                ()
        | None -> ()

    /// A child was spawned.
    let spawn (logger: ILogger option) (program: string) (pid: int option) (runId: string) =
        emitSafely logger (fun log ->
            let pidText =
                match pid with
                | Some p -> string p
                | None -> "?"

            spawnMessage.Invoke(log, program, pidText, runId, null))

    /// A run finished (exit reaped).
    let exit (logger: ILogger option) (program: string) (outcome: Outcome) (duration: TimeSpan) (runId: string) =
        emitSafely logger (fun log ->
            exitMessage.Invoke(log, program, string outcome, duration.TotalMilliseconds, runId, null))

    /// A run was killed for exceeding its timeout.
    let timeout (logger: ILogger option) (program: string) (deadline: TimeSpan) (runId: string) =
        emitSafely logger (fun log -> timeoutMessage.Invoke(log, program, deadline.TotalMilliseconds, runId, null))

    /// A run was killed for producing no output for its idle deadline.
    let idleTimeout (logger: ILogger option) (program: string) (idle: TimeSpan) (runId: string) =
        emitSafely logger (fun log -> idleTimeoutMessage.Invoke(log, program, idle.TotalMilliseconds, runId, null))

    /// A failed run is being retried.
    let retry (logger: ILogger option) (program: string) (attempt: int) (delay: TimeSpan) (runId: string) =
        emitSafely logger (fun log -> retryMessage.Invoke(log, program, attempt, delay.TotalMilliseconds, runId, null))

    /// A supervisor is restarting its child.
    let supervisorRestart (logger: ILogger option) (program: string) (restart: int) (delay: TimeSpan) =
        emitSafely logger (fun log ->
            supervisorRestartMessage.Invoke(log, program, restart, delay.TotalMilliseconds, null))

    /// A supervisor's failure-storm guard paused restarts.
    let stormPause (logger: ILogger option) (program: string) (pause: TimeSpan) =
        emitSafely logger (fun log -> stormPauseMessage.Invoke(log, program, pause.TotalMilliseconds, null))
