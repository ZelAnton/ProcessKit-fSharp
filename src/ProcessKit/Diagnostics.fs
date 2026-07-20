namespace ProcessKit

open Microsoft.Extensions.Logging

/// The well-known names and identifiers of ProcessKit's `System.Diagnostics` / `Microsoft.Extensions.Logging`
/// observability surface, so a consumer references them without a magic string or number — e.g.
/// `builder.AddSource(ProcessKitDiagnostics.ActivitySourceName)` /
/// `builder.AddMeter(ProcessKitDiagnostics.MeterName)`, or filter logs by
/// `ProcessKitDiagnostics.Events.ProcessExited`.
///
/// **Security:** neither a log message, a trace span tag, nor a metric tag ever carries argv or
/// environment **values** — only the program *name* and non-secret facts (outcome, duration, exit code /
/// signal, pid, run id).
[<RequireQualifiedAccess>]
module ProcessKitDiagnostics =

    /// The name of the `ActivitySource` ProcessKit emits one span per completed run on (`"ProcessKit"`).
    [<Literal>]
    let ActivitySourceName = "ProcessKit"

    /// The name of the `Meter` ProcessKit publishes its run/retry/supervisor instruments on (`"ProcessKit"`).
    [<Literal>]
    let MeterName = "ProcessKit"

    /// The stable `EventId`s (name + number) ProcessKit tags its `ILogger` lifecycle events with, so a
    /// consumer can filter or route by id. The numbers are part of the public contract — kept stable.
    [<RequireQualifiedAccess>]
    module Events =

        /// A child process was spawned (Debug). Carries program, pid, run id.
        let ProcessSpawned = EventId(1, "ProcessSpawned")

        /// A run finished — its exit was reaped (Debug). Carries program, outcome, duration, run id.
        let ProcessExited = EventId(2, "ProcessExited")

        /// A run was killed for exceeding its timeout (Warning). Carries program, timeout, run id.
        let ProcessTimedOut = EventId(3, "ProcessTimedOut")

        /// A failed run is being retried (Debug). Carries program, attempt, delay, run id.
        let ProcessRetry = EventId(4, "ProcessRetry")

        /// A supervisor is restarting its child (Debug). Carries program, restart number, delay.
        let SupervisorRestart = EventId(5, "SupervisorRestart")

        /// A supervisor's failure-storm guard paused restarts (Warning). Carries program, pause.
        let SupervisorStormPause = EventId(6, "SupervisorStormPause")

        /// A supervisor's liveness probe found the live child unresponsive and restarted it (Warning).
        /// Carries program and the consecutive-failure threshold that tripped.
        let SupervisorLivenessRestart = EventId(7, "SupervisorLivenessRestart")
