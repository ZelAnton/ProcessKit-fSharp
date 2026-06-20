namespace ProcessKit

open System
open Microsoft.Extensions.Logging

/// Internal structured logging for the process lifecycle. Every event is a no-op when no `ILogger`
/// is configured (the common case), so logging is opt-in and free otherwise.
///
/// **Security: argv and environment are never logged.** A command line carries secrets (a
/// `--password=…` flag, a token); the environment carries credentials. Only the program *name* and
/// non-secret lifecycle facts (pid, outcome, durations, restart counts) are emitted.
module internal Log =

    let private enabled (logger: ILogger option) (level: LogLevel) =
        match logger with
        | Some log when log.IsEnabled level -> Some log
        | _ -> None

    /// A child was spawned.
    let spawn (logger: ILogger option) (program: string) (pid: int option) =
        match enabled logger LogLevel.Debug with
        | Some log ->
            let pidText =
                match pid with
                | Some p -> string p
                | None -> "?"

            log.LogDebug("processkit: spawned {Program} (pid {Pid})", program, pidText)
        | None -> ()

    /// A run finished (exit reaped).
    let exit (logger: ILogger option) (program: string) (outcome: Outcome) (duration: TimeSpan) =
        match enabled logger LogLevel.Debug with
        | Some log ->
            log.LogDebug(
                "processkit: {Program} finished ({Outcome}) in {DurationMs}ms",
                program,
                string outcome,
                duration.TotalMilliseconds
            )
        | None -> ()

    /// A run was killed for exceeding its timeout.
    let timeout (logger: ILogger option) (program: string) (deadline: TimeSpan) =
        match logger with
        | Some log ->
            log.LogWarning("processkit: {Program} timed out after {TimeoutMs}ms", program, deadline.TotalMilliseconds)
        | None -> ()

    /// A failed run is being retried.
    let retry (logger: ILogger option) (program: string) (attempt: int) (delay: TimeSpan) =
        match enabled logger LogLevel.Debug with
        | Some log ->
            log.LogDebug(
                "processkit: retrying {Program} (attempt {Attempt}) after {DelayMs}ms",
                program,
                attempt,
                delay.TotalMilliseconds
            )
        | None -> ()

    /// A supervisor is restarting its child.
    let supervisorRestart (logger: ILogger option) (program: string) (restart: int) (delay: TimeSpan) =
        match enabled logger LogLevel.Debug with
        | Some log ->
            log.LogDebug(
                "processkit: supervisor restarting {Program} (restart {Restart}) after {DelayMs}ms",
                program,
                restart,
                delay.TotalMilliseconds
            )
        | None -> ()

    /// A supervisor's failure-storm guard paused restarts.
    let stormPause (logger: ILogger option) (program: string) (pause: TimeSpan) =
        match logger with
        | Some log ->
            log.LogWarning(
                "processkit: supervisor failure storm for {Program} — pausing restarts for {PauseMs}ms",
                program,
                pause.TotalMilliseconds
            )
        | None -> ()
