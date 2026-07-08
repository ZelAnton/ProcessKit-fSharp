namespace ProcessKit

open System
open System.Diagnostics
open System.Threading
open Microsoft.Extensions.Logging

/// The observability lifecycle of one run — start, then exactly one of "reached a terminal verb"
/// (`Conclude`) or "left in flight without one" (`Abandon`) — collapsed into a single once-guarded
/// type so `RunningProcess` and `PipelineRunner` share the exact same `runs.active`/
/// `runs.completed`/span accounting instead of maintaining two hand-rolled, divergent copies of it
/// (see T-041; the `runs.active` leak this once-guard fixed is T-007). `Start` pairs with exactly one
/// of `Conclude`/`Abandon` — whichever the caller reaches first decides whether the run's end counts
/// as `runs.completed`, but either way the in-flight mark clears exactly once.
type internal RunTelemetryScope
    private (program: string, runId: string, startTime: DateTime, parentContext: ActivityContext) =

    // 0 = not yet ended; `Interlocked.Exchange` flips it to 1 for whichever of `Conclude`/`Abandon`
    // gets there first, so the run's end is counted exactly once no matter which path wins the race
    // (a terminal verb vs. the handle being disposed/abandoned first).
    let mutable endedFlag = 0

    let tryClaimEnd () = Interlocked.Exchange(&endedFlag, 1) = 0

    /// Start a run: count it as started + in-flight (`Diag.runStarted`) and capture the ambient
    /// `Activity` now (at spawn), so a completion span emitted later still nests under it. Does NOT
    /// log the spawn event itself (`Log.spawn`) — callers emit that alongside, since its pid/timing
    /// details differ between a single process and a pipeline.
    static member Start(program: string, runId: string, startTime: DateTime) : RunTelemetryScope =
        let parentContext = Diag.runStarted program
        RunTelemetryScope(program, runId, startTime, parentContext)

    /// The run reached a terminal verb: record it as completed (metrics + backdated trace span) and
    /// clear the in-flight mark — exactly once; a no-op if `Abandon` already won the race, or this was
    /// already called. `timeout`, when supplied, logs `Log.timeout` first (the pipeline's own,
    /// explicit deadline-kill event) before `Log.exit`; a single command's `Timeouts.raceTimeout`
    /// already logs its own timeout independently of this scope, so it never supplies one.
    member _.Conclude
        (logger: ILogger option, outcome: Outcome, pid: int option, duration: TimeSpan, ?timeout: TimeSpan)
        =
        if tryClaimEnd () then
            match timeout with
            | Some deadline -> Log.timeout logger program deadline runId
            | None -> ()

            Log.exit logger program outcome duration runId
            Diag.runCompleted program runId outcome pid startTime duration parentContext
            Diag.runEnded program

    /// The run left "in flight" without ever reaching a terminal verb (a streaming/event-driven
    /// handle that was only consumed and dropped, or a later pipeline stage that failed to spawn) —
    /// clears `runs.active` only, not counted as `runs.completed`, no span. A no-op once `Conclude`
    /// already ran.
    member _.Abandon() =
        if tryClaimEnd () then
            Diag.runEnded program
