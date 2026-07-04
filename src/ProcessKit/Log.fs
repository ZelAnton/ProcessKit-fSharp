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

    /// A child was spawned.
    let spawn (logger: ILogger option) (program: string) (pid: int option) (runId: string) =
        match logger with
        | Some log ->
            let pidText =
                match pid with
                | Some p -> string p
                | None -> "?"

            spawnMessage.Invoke(log, program, pidText, runId, null)
        | None -> ()

    /// A run finished (exit reaped).
    let exit (logger: ILogger option) (program: string) (outcome: Outcome) (duration: TimeSpan) (runId: string) =
        match logger with
        | Some log -> exitMessage.Invoke(log, program, string outcome, duration.TotalMilliseconds, runId, null)
        | None -> ()

    /// A run was killed for exceeding its timeout.
    let timeout (logger: ILogger option) (program: string) (deadline: TimeSpan) (runId: string) =
        match logger with
        | Some log -> timeoutMessage.Invoke(log, program, deadline.TotalMilliseconds, runId, null)
        | None -> ()

    /// A failed run is being retried.
    let retry (logger: ILogger option) (program: string) (attempt: int) (delay: TimeSpan) (runId: string) =
        match logger with
        | Some log -> retryMessage.Invoke(log, program, attempt, delay.TotalMilliseconds, runId, null)
        | None -> ()

    /// A supervisor is restarting its child.
    let supervisorRestart (logger: ILogger option) (program: string) (restart: int) (delay: TimeSpan) =
        match logger with
        | Some log -> supervisorRestartMessage.Invoke(log, program, restart, delay.TotalMilliseconds, null)
        | None -> ()

    /// A supervisor's failure-storm guard paused restarts.
    let stormPause (logger: ILogger option) (program: string) (pause: TimeSpan) =
        match logger with
        | Some log -> stormPauseMessage.Invoke(log, program, pause.TotalMilliseconds, null)
        | None -> ()
