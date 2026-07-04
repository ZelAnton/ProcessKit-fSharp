namespace ProcessKit

open System
open System.Diagnostics
open System.Diagnostics.Metrics
open System.Threading

/// Internal `System.Diagnostics` instrumentation — a shared `ActivitySource` (distributed tracing) and
/// `Meter` (metrics), both free when nothing is listening. Emitted alongside the `Log` events at the same
/// lifecycle points. **argv and environment values are never recorded** — only the program name and
/// non-secret facts (outcome, duration, exit code/signal, pid, run id).
module internal Diag =

    // Process-wide singletons; creating an Activity/measurement is a cheap no-op when no listener subscribes.
    let activitySource = new ActivitySource(ProcessKitDiagnostics.ActivitySourceName)

    let private meter = new Meter(ProcessKitDiagnostics.MeterName)

    // Units follow the OpenTelemetry/UCUM convention: dimensionless counts use a `{...}` annotation
    // (bare words would be emitted as literal unit suffixes by exporters), and the duration histogram is
    // in **seconds** (the OTel norm for `*.duration`), so dashboards calibrated to it aren't off by 1000×.
    let private runsStarted =
        meter.CreateCounter<int64>("processkit.runs.started", "{run}", "Process runs started.")

    let private runsCompleted =
        meter.CreateCounter<int64>("processkit.runs.completed", "{run}", "Process runs that reached a terminal verb.")

    let private runsActive =
        meter.CreateUpDownCounter<int64>("processkit.runs.active", "{run}", "Process runs currently in flight.")

    let private runDuration =
        meter.CreateHistogram<double>("processkit.run.duration", "s", "Wall-clock duration of a completed run.")

    let private retriesCounter =
        meter.CreateCounter<int64>("processkit.retries", "{retry}", "Run retries attempted.")

    let private restartsCounter =
        meter.CreateCounter<int64>("processkit.supervisor.restarts", "{restart}", "Supervisor restarts.")

    let private stormPausesCounter =
        meter.CreateCounter<int64>("processkit.supervisor.storm_pauses", "{pause}", "Supervisor failure-storm pauses.")

    // A monotonic, per-process run correlation id. Compact hex so it reads cleanly in a log line and
    // ties one run's events (spawn/exit/timeout/retry) together across a concurrent fleet. Per-process
    // (not globally unique): a log sink already scopes by process, and distributed correlation is the
    // trace's job.
    let private runIdCounter = ref 0L

    let newRunId () : string =
        let n = Interlocked.Increment(&runIdCounter.contents)
        n.ToString("x8")

    // Bounded-cardinality tags: program name only (never argv), plus a small closed set of outcome labels.
    let private programTag (program: string) : TagList =
        let mutable tags = TagList()
        tags.Add("processkit.program", program)
        tags

    let internal outcomeLabel (outcome: Outcome) : string =
        match outcome with
        | Outcome.Exited _ -> "exited"
        | Outcome.Signalled _ -> "signalled"
        | Outcome.TimedOut -> "timedout"

    let private outcomeTags (program: string) (label: string) : TagList =
        let mutable tags = TagList()
        tags.Add("processkit.program", program)
        tags.Add("processkit.outcome", label)
        tags

    /// A run was spawned: count it and mark it in flight. Returns the `Activity` context current *now*
    /// (at spawn) so the backdated span emitted at completion still nests under it.
    let runStarted (program: string) : ActivityContext =
        let mutable startedTags = programTag program
        let mutable activeTags = programTag program
        runsStarted.Add(1L, &startedTags)
        runsActive.Add(1L, &activeTags)

        match Activity.Current with
        | null -> Unchecked.defaultof<ActivityContext>
        | current -> current.Context

    // Emit one completed-run span, backdated to the spawn time so its duration is the real run length.
    // Created at completion (not held open across the run) so there is no lifecycle to leak: an abandoned
    // run simply never gets a span. Free when no listener.
    let private emitSpan
        (program: string)
        (runId: string)
        (outcome: Outcome)
        (pid: int option)
        (startTime: DateTime)
        (parentContext: ActivityContext)
        =
        if activitySource.HasListeners() then
            let start = DateTimeOffset(startTime.ToUniversalTime(), TimeSpan.Zero)

            match
                activitySource.StartActivity("processkit.run", ActivityKind.Internal, parentContext, startTime = start)
            with
            | null -> ()
            | activity ->
                activity.SetTag("processkit.program", program) |> ignore
                activity.SetTag("processkit.run_id", runId) |> ignore
                activity.SetTag("processkit.outcome", outcomeLabel outcome) |> ignore

                match outcome.Code with
                | Some code -> activity.SetTag("processkit.exit_code", box code) |> ignore
                | None -> ()

                match outcome.Signal with
                | Some signal -> activity.SetTag("processkit.signal", box signal) |> ignore
                | None -> ()

                match pid with
                | Some p -> activity.SetTag("processkit.pid", box p) |> ignore
                | None -> ()

                // A non-accepted exit is not an *error* in ProcessKit's model (a non-zero exit is data),
                // so leave the span status unset rather than forcing Error — the outcome tag carries the fact.
                activity.Dispose() // ends the span at "now" → duration = now - startTime

    /// A run reached a terminal verb: record the completion counter, its duration, clear the in-flight
    /// mark, and emit the (backdated) trace span.
    let runCompleted
        (program: string)
        (runId: string)
        (outcome: Outcome)
        (pid: int option)
        (startTime: DateTime)
        (duration: TimeSpan)
        (parentContext: ActivityContext)
        =
        let label = outcomeLabel outcome
        let mutable completedTags = outcomeTags program label
        let mutable activeTags = programTag program
        runsCompleted.Add(1L, &completedTags)
        runDuration.Record(duration.TotalSeconds, &completedTags)
        runsActive.Add(-1L, &activeTags)
        emitSpan program runId outcome pid startTime parentContext

    let retried (program: string) =
        let mutable tags = programTag program
        retriesCounter.Add(1L, &tags)

    let supervisorRestarted (program: string) =
        let mutable tags = programTag program
        restartsCounter.Add(1L, &tags)

    let stormPaused (program: string) =
        let mutable tags = programTag program
        stormPausesCounter.Add(1L, &tags)
