namespace ProcessKit

open System
open System.Collections.Generic
open System.Diagnostics
open System.Net.Http
open System.Runtime.InteropServices
open System.Threading
open System.Threading.Tasks
open System.Threading.Channels

/// When the supervisor restarts an exited child. In every case `Supervisor.StopWhen` and
/// `Supervisor.MaxRestarts` can end supervision first.
[<RequireQualifiedAccess; NoComparison>]
type RestartPolicy =

    /// Restart after every completed run, clean or not.
    | Always

    /// Restart only after a *crash* — a run that is not a success (`ProcessResult.IsSuccess` is
    /// false): a non-zero exit, a timeout, a signal kill, or a failure to spawn. A successful run
    /// (exit 0) ends supervision.
    | OnCrash

    /// Never restart: run the child once and report its outcome.
    | Never

/// Why supervision ended.
[<RequireQualifiedAccess; NoComparison>]
type StopReason =

    /// The `Supervisor.StopWhen` predicate matched a run.
    | Predicate

    /// The `RestartPolicy` was satisfied — a clean exit under `OnCrash`, or the single `Never` run
    /// completing.
    | PolicySatisfied

    /// The `Supervisor.MaxRestarts` budget ran out while the policy still wanted another restart.
    | RestartsExhausted

    /// The `Supervisor.GiveUpWhen` classifier recognized a crash as *permanent* — the supervisor
    /// stopped instead of restarting it forever. Only reported for a crashed run that produced a
    /// `ProcessResult` (the classifier still receives that crash's `ProcessError` projection, via
    /// `ProcessResult.FailureError`); a permanent failure that never produced a result (a spawn/IO
    /// failure the classifier also recognizes) has no result to report and instead surfaces
    /// directly as `RunAsync`'s `Error`, same as an exhausted budget on that path.
    | GaveUp

    /// A `SupervisionSession.StopAsync` graceful stop ended supervision: the current incarnation was
    /// stopped through its own graceful path (`RunningProcess.StopAsync`) and the loop concluded
    /// cleanly. Not a crash and not a token cancellation — the reported `SupervisionOutcome.FinalResult`
    /// is the honest result of that last, deliberately-stopped incarnation. Only reported when some
    /// incarnation did produce a result: a stop landing before any of them did has none to report and,
    /// like an exhausted budget on that path, surfaces directly as `RunAsync`'s `Error` instead — the
    /// last failure that kept the child from starting, or `ProcessError.Cancelled` when no incarnation
    /// was ever started at all.
    | Stopped

/// Why the supervisor is restarting an incarnation — the `SupervisorRestartEvent.Cause` a live
/// `OnRestart` handler can branch on to tell an ordinary exit/crash restart apart from one the
/// liveness probe forced.
[<RequireQualifiedAccess; NoComparison>]
type RestartCause =

    /// The incarnation ended on its own — a completed run the `RestartPolicy` chose to restart (a
    /// crash, a timeout, or a retried transient runner error). The default cause for every restart
    /// when no liveness probe is configured.
    | Exit

    /// A configured liveness probe (`Supervisor.LivenessHttp`/`LivenessCheck`/`LivenessMemory`) found the *live* child
    /// unhealthy for the configured number of consecutive attempts, so the supervisor gracefully
    /// stopped it and restarted it through the ordinary policy/backoff path.
    | Liveness

/// A single restart, reported live from the supervision loop (see `Supervisor.OnRestart`) — not to
/// be confused with the final `SupervisionOutcome.Restarts` count.
///
/// Sealed with an internal constructor so it can gain fields without breaking the frozen API.
[<Sealed>]
type SupervisorRestartEvent internal (program: string, restart: int, delay: TimeSpan, cause: RestartCause) =

    /// The supervised command's program name.
    member _.Program = program

    /// The 1-based lifetime restart number — matches `SupervisionOutcome.Restarts` once this
    /// restart becomes the last one.
    member _.Restart = restart

    /// The backoff delay the supervisor is about to sleep out before this restart.
    member _.Delay = delay

    /// Why this restart is happening — `RestartCause.Exit` for an ordinary completed-run restart, or
    /// `RestartCause.Liveness` when a liveness probe found the live child unresponsive. Lets a handler
    /// alert on a hung service distinctly from an ordinary crash restart.
    member _.Cause = cause

/// A single failure-storm pause, reported live from the supervision loop (see
/// `Supervisor.OnStormPause`) — not to be confused with the final `SupervisionOutcome.StormPauses`
/// count.
///
/// Sealed with an internal constructor so it can gain fields without breaking the frozen API.
[<Sealed>]
type SupervisorStormPauseEvent internal (program: string, stormPause: int, delay: TimeSpan) =

    /// The supervised command's program name.
    member _.Program = program

    /// The 1-based lifetime pause number — matches `SupervisionOutcome.StormPauses` once this pause
    /// becomes the last one.
    member _.StormPause = stormPause

    /// The jittered pause duration the supervisor is about to sleep out.
    member _.Delay = delay

/// What a finished supervision reports — the last run plus the keeper's telemetry.
///
/// Sealed with an internal constructor so it can gain fields without breaking the frozen API.
[<Sealed>]
type SupervisionOutcome
    internal (finalResult: ProcessResult<string>, restarts: int, stopped: StopReason, stormPauses: int) =

    /// The result of the final run (the one that ended supervision).
    member _.FinalResult = finalResult

    /// How many times the child was *re*-run (the first run is not a restart): `Restarts = 2` means
    /// three runs happened.
    member _.Restarts = restarts

    /// Why supervision stopped.
    member _.Stopped = stopped

    /// How many times the failure-storm guard paused restarts (always `0` unless `StormPause` is set).
    member _.StormPauses = stormPauses

/// Internal supervision math: capture defaulting, exponential backoff, jitter, and the decaying
/// failure score behind the storm guard. Pure functions, unit-tested directly.
module internal Supervision =

    /// Default per-incarnation capture tail for a supervised command whose own policy is unbounded.
    /// A supervised process can be long-lived and chatty, so capturing its *entire* output risks
    /// unbounded heap — keep a bounded tail (the most recent lines) by default instead.
    [<Literal>]
    let DefaultSupervisionTail = 1000

    /// Default capacity of the opt-in live event buffer (`Supervisor.Events()`), in events not yet read
    /// by the consumer. Deep enough that an ordinary consumer never lags (a whole crash-restart cycle
    /// costs three events), shallow enough that an *unread* stream can never grow the supervisor's heap.
    [<Literal>]
    let DefaultEventCapacity = 128

    /// The capture policy to apply to each incarnation: respect an explicit bounded/fail-loud
    /// command policy, but bound an unbounded line count to a tail. Only the line cap is filled in
    /// — the overflow *mode* and any byte cap are preserved, so an unbounded `Error` (fail-loud)
    /// command stays fail-loud rather than silently switching to `DropOldest`.
    let defaultCapture (command: Command) : OutputBufferPolicy =
        let policy = command.Config.OutputBuffer

        match policy.MaxLines with
        | Some _ -> policy
        | None -> OutputBufferPolicy(Some DefaultSupervisionTail, policy.MaxBytes, policy.Overflow)

    /// A safe ceiling for any computed delay, so jitter never overflows `Task.Delay`.
    let maxDelay = Backoff.maxDelay

    /// `base × factor^n`, capped at `cap`.
    let backoffDelay (baseDelay: TimeSpan) (factor: float) (n: int) (cap: TimeSpan) : TimeSpan =
        Backoff.exponentialDelay baseDelay factor n cap

    /// A pseudo-random factor in `[0.5, 1.5)`.
    let jitterFactor () =
        Backoff.jitterFactor (fun () -> Random.Shared.NextDouble())

    /// Multiply `delay` by a uniform random factor in `[0.5, 1.5)` when `enabled`, always clamped to
    /// `[0, maxDelay]` so the result is safe to hand to `Task.Delay` — even with jitter off and a
    /// large `MaxBackoff` / `StormPause`, the delay can never overflow the BCL timer.
    let applyJitter (delay: TimeSpan) (enabled: bool) : TimeSpan =
        Backoff.applyJitter delay enabled (fun () -> Random.Shared.NextDouble())

    /// Fold one failure into the decaying score: the previous score halves every `halfLife` of
    /// elapsed time, then the new failure adds `1`. A zero half-life keeps no history (every
    /// failure scores exactly `1.0`); a non-finite previous score resets rather than propagating.
    let decayedFailureScore (prev: float) (elapsedSeconds: float) (halfLife: TimeSpan) : float =
        if halfLife <= TimeSpan.Zero then
            1.0
        else
            let halflives = elapsedSeconds / halfLife.TotalSeconds
            let decayed = prev * (0.5 ** halflives)

            if Double.IsFinite decayed then decayed + 1.0 else 1.0

module internal Liveness =

    /// A non-positive interval is a configuration typo, but rejecting it would prevent supervisor
    /// startup; clamp it to a real timer tick so the monitor keeps its startup-delay contract instead
    /// of spinning on `Task.Delay(TimeSpan.Zero)`.
    let minimumLivenessInterval = TimeSpan.FromMilliseconds 1.0

    let clampInterval (interval: TimeSpan) =
        if interval <= TimeSpan.Zero then
            minimumLivenessInterval
        else
            interval

/// How the supervisor checks whether a *live* incarnation is still healthy: an HTTP endpoint, an
/// arbitrary async predicate, or an attributable whole-tree memory sample. Internal — built through
/// the `Supervisor.LivenessHttp`/`LivenessCheck`/`LivenessMemory` builder methods. Endpoint probes funnel
/// through the shared readiness poll/deadline core (`ReadinessProbe.waitForCoreUsing`).
[<RequireQualifiedAccess; NoComparison>]
type internal LivenessProbe =

    /// Poll `uri` with HTTP GET each attempt; the child is healthy when a response satisfies the check.
    | Http of uri: Uri * isSatisfactory: Func<HttpResponseMessage, bool> * client: HttpClient option

    /// Evaluate an arbitrary async predicate each attempt; the child is healthy when it returns `true`.
    | Custom of probe: (unit -> Task<bool>)

    /// Sample attributable whole-tree peak memory since the incarnation started; healthy while the
    /// peak is at or below `maxBytes`. This is intentionally not a current-working-set sample.
    | Memory of maxBytes: int64

/// The immutable configuration behind a `Supervisor`. Internal — built through the `Supervisor`
/// builder.
type internal SupervisorConfig =
    { Command: Command
      Runner: IProcessRunner
      Policy: RestartPolicy
      MaxRestarts: int option
      BackoffBase: TimeSpan
      BackoffFactor: float
      MaxBackoff: TimeSpan
      Jitter: bool
      FailureDecay: TimeSpan
      FailureThreshold: float
      StormPause: TimeSpan option
      StopWhen: (ProcessResult<string> -> bool) option
      GiveUpWhen: (ProcessError -> bool) option
      OnRestart: (SupervisorRestartEvent -> unit) option
      OnStormPause: (SupervisorStormPauseEvent -> unit) option
      Capture: OutputBufferPolicy
      // The opt-in live event stream (`Supervisor.Events`): the bounded buffer's capacity, or `None`
      // (the default) for a session that publishes no events at all — the loop then allocates neither a
      // channel nor a single event object, so `RunAsync` and every existing consumer are untouched.
      EventCapacity: int option
      // Liveness supervision (off unless `Liveness` is set): periodically probe the live incarnation and,
      // after `LivenessFailures` consecutive failed attempts, gracefully stop it (with `LivenessGrace`)
      // so the ordinary restart path takes over. `LivenessInterval` is the gap between attempts;
      // `LivenessTimeout` bounds one attempt.
      Liveness: LivenessProbe option
      LivenessInterval: TimeSpan
      LivenessFailures: int
      LivenessTimeout: TimeSpan
      LivenessGrace: TimeSpan
      LivenessDelay: TimeSpan -> CancellationToken -> Task
      // The clock seam: `Now` is a monotonic reading in seconds (only differences matter); `Sleep`
      // waits out a delay. Real implementations by default; tests inject a virtual clock that
      // advances `Now` when it `Sleep`s, so backoff/storm timing is deterministic.
      Now: unit -> float
      Sleep: TimeSpan -> CancellationToken -> Task }

module internal SupervisorConfig =

    let realNow (timeProvider: TimeProvider) () =
        float (timeProvider.GetTimestamp()) / float timeProvider.TimestampFrequency

    let realSleep (timeProvider: TimeProvider) (delay: TimeSpan) (cancellationToken: CancellationToken) : Task =
        task {
            try
                do! Task.Delay(delay, timeProvider, cancellationToken)
            with :? OperationCanceledException ->
                // Cancelled during a backoff / storm pause; the supervisor loop's top-of-iteration
                // token check converts this into a terminal `Cancelled` result.
                ()
        }
        :> Task

    let create (command: Command) =
        { Command = command
          Runner = JobRunner()
          Policy = RestartPolicy.OnCrash
          MaxRestarts = None
          BackoffBase = TimeSpan.FromMilliseconds 200.0
          BackoffFactor = 2.0
          MaxBackoff = TimeSpan.FromSeconds 30.0
          Jitter = true
          FailureDecay = TimeSpan.FromSeconds 30.0
          FailureThreshold = 5.0
          StormPause = None
          StopWhen = None
          GiveUpWhen = None
          OnRestart = None
          OnStormPause = None
          Capture = Supervision.defaultCapture command
          EventCapacity = None
          Liveness = None
          LivenessInterval = TimeSpan.FromSeconds 10.0
          LivenessFailures = 3
          LivenessTimeout = TimeSpan.FromSeconds 2.0
          LivenessGrace = TimeSpan.FromSeconds 2.0
          LivenessDelay =
            fun delay cancellationToken -> Task.Delay(delay, command.Config.TimeProvider, cancellationToken)
          Now = realNow command.Config.TimeProvider
          Sleep = realSleep command.Config.TimeProvider }

/// A consistent, point-in-time snapshot of a `SupervisionSession`'s live state — read atomically, so
/// every field agrees with the others (no torn read across a concurrent update from the supervision
/// loop). Only non-secret facts are exposed (activity, counts, the current child's pid/start time);
/// argv and environment values never appear here, matching `ProcessKitDiagnostics`'s taxonomy.
///
/// Sealed with an internal constructor so it can gain fields without breaking the frozen API.
[<Sealed>]
type SupervisionStatus
    internal (isActive: bool, restarts: int, isStormPaused: bool, pid: int option, startTime: DateTime option) =

    /// Whether the supervision loop is still running: `true` from `StartAsync` until supervision ends
    /// (naturally, via a graceful `SupervisionSession.StopAsync`, or by token cancellation), `false`
    /// once the final `SupervisionOutcome` (or terminal error) has been produced.
    member _.IsActive = isActive

    /// How many times the child has been *re*-run so far, live — mirrors `SupervisionOutcome.Restarts`
    /// but updates as each restart happens rather than only once supervision ends. The first run is not
    /// a restart, so `0` while the first incarnation is alive.
    member _.Restarts = restarts

    /// Whether restarts are currently paused by the failure-storm guard (`Supervisor.StormPause`) —
    /// `true` only while a storm pause is being slept out. Always `false` when `StormPause` is unset.
    member _.IsStormPaused = isStormPaused

    /// The OS process id of the current live incarnation, or `None` when no child is alive right now
    /// (between incarnations, during a backoff/storm pause, or once supervision has ended) or when the
    /// runner exposes no live handle (a scripted test double).
    member _.Pid = pid

    /// When the current live incarnation started, including a capture-only incarnation whose `Pid` is
    /// unavailable, or `None` when no incarnation is active right now.
    member _.StartTime = startTime

/// Which supervision transition a `SupervisionEvent` reports — the discriminator to branch on when
/// consuming `SupervisionSession.EventsAsync`.
///
/// A plain .NET enum on purpose: this taxonomy is meant to *grow*, and a new kind must not break a
/// consumer that was written against an earlier version. Existing kinds are never renumbered, renamed,
/// or repurposed; new ones are appended with the next free value. Both languages already force the
/// habit that makes that safe — F# requires a wildcard branch when matching an enum, and a C# `switch`
/// expression needs a discard arm — so treat an unrecognized kind as "something newer happened" (the
/// event's `Name` still identifies it) rather than as an error.
type SupervisionEventKind =

    /// An incarnation was launched: `Attempt` is its 1-based number, `Pid` the live child's process id
    /// (`None` for a capture-only runner, which exposes no live handle).
    | IncarnationStarted = 1

    /// An incarnation produced a `ProcessResult`: `Attempt`, `Outcome`, `Duration`, and `IsSuccess`
    /// (the command's `OkCodes` verdict, not merely "exit 0").
    | IncarnationFinished = 2

    /// An incarnation ended without producing a `ProcessResult` at all — it never started, or its
    /// capture failed. `Attempt` and the coarse `FailureKind` say which incarnation and what class of
    /// failure; the failure's message and any captured output are deliberately not carried.
    | IncarnationFailed = 3

    /// A restart was scheduled: `Restart` is its 1-based lifetime number, `Delay` the (jittered)
    /// backoff about to be slept out, and `Cause` distinguishes an ordinary exit restart from one a
    /// liveness probe forced. Reported before the delay, exactly like the `OnRestart` callback.
    | RestartScheduled = 4

    /// The failure-storm guard paused restarts: `StormPause` is its 1-based number and `Delay` the
    /// (jittered) pause about to be slept out. Reported before the pause, like `OnStormPause`.
    | StormPaused = 5

    /// A liveness/health probe ended the current incarnation: `Attempt` is that incarnation, and
    /// `IsTerminal` is `false` when the unhealthy streak tripped (the ordinary restart path decides
    /// what happens next) or `true` when the probe itself failed fatally and supervision will end.
    /// Reported once per decision, not once per failed attempt.
    | HealthCheckFailed = 6

    /// The `GiveUpWhen` classifier declared `Attempt`'s failure permanent, so supervision stops
    /// instead of restarting it.
    | GaveUp = 7

    /// Supervision ended with a `SupervisionOutcome`; `Reason` is that outcome's `StopReason`. The
    /// last event of a successful supervision.
    | Stopped = 8

    /// Supervision ended with a terminal error instead of an outcome; `FailureKind` is that error's
    /// coarse class. The last event of a failed supervision.
    | SupervisionFailed = 9

    /// The consumer fell behind the bounded buffer and `DroppedEvents` older events were discarded to
    /// make room. Synthesized on the reading side immediately before the oldest event that survived,
    /// so a gap is always explicit — never a silent loss. See `SupervisionSession.DroppedEventCount`
    /// for the lifetime total.
    | EventsDropped = 10

/// The per-kind payload behind a `SupervisionEvent`. Internal, and a record rather than a positional
/// argument list, so a future field is one `with`-updated line in the one factory that carries it
/// instead of another argument on every construction site.
type internal SupervisionEventPayload =
    { Kind: SupervisionEventKind
      Program: string
      Attempt: int option
      Pid: int option
      Outcome: Outcome option
      Duration: TimeSpan option
      IsSuccess: bool option
      FailureKind: string option
      Restart: int option
      Delay: TimeSpan option
      Cause: RestartCause option
      StormPause: int option
      IsTerminal: bool option
      Reason: StopReason option
      DroppedEvents: int64 option }

module internal SupervisionEventPayload =

    /// The stable machine identifier of an event kind (`incarnation_started`, `restart_scheduled`, …) —
    /// the single spelling of the supervision event vocabulary, read by `SupervisionEvent.Name` and by
    /// the generator behind `spec/identifiers.json`. It lives here rather than in `StableIdentifiers`
    /// because `SupervisionEventKind` is declared in this file, long after that one in compile order.
    ///
    /// A .NET enum can hold a value outside its declared cases, so F# requires the final arm even when
    /// every declared kind is covered; that arm is unreachable for any kind this library constructs.
    /// The completeness guard for this vocabulary is therefore not the compiler but
    /// `IdentifiersManifestTests`, which enumerates the declared values by reflection and asks this
    /// function for each: a kind added without a name here fails that test rather than passing silently.
    let eventName (kind: SupervisionEventKind) : string =
        match kind with
        | SupervisionEventKind.IncarnationStarted -> "incarnation_started"
        | SupervisionEventKind.IncarnationFinished -> "incarnation_finished"
        | SupervisionEventKind.IncarnationFailed -> "incarnation_failed"
        | SupervisionEventKind.RestartScheduled -> "restart_scheduled"
        | SupervisionEventKind.StormPaused -> "storm_paused"
        | SupervisionEventKind.HealthCheckFailed -> "health_check_failed"
        | SupervisionEventKind.GaveUp -> "gave_up"
        | SupervisionEventKind.Stopped -> "stopped"
        | SupervisionEventKind.SupervisionFailed -> "supervision_failed"
        | SupervisionEventKind.EventsDropped -> "events_dropped"
        | unnamed ->
            raise (
                ArgumentOutOfRangeException(
                    nameof kind,
                    int unnamed,
                    "SupervisionEventKind has no stable identifier for this value; name it in SupervisionEventPayload.eventName and regenerate spec/identifiers.json."
                )
            )

    /// A payload carrying only what every event carries; each factory fills in its own fields.
    let create (kind: SupervisionEventKind) (program: string) =
        { Kind = kind
          Program = program
          Attempt = None
          Pid = None
          Outcome = None
          Duration = None
          IsSuccess = None
          FailureKind = None
          Restart = None
          Delay = None
          Cause = None
          StormPause = None
          IsTerminal = None
          Reason = None
          DroppedEvents = None }

    /// The stable, coarse class of a failure — the union case's own identifier and nothing else.
    /// Deliberately not `ProcessError.Message` and never the error value itself: several cases carry
    /// the child's captured stdout/stderr (`Exit`/`Signalled`/`Timeout`) or a detail string built from
    /// an OS message, none of which belongs in a fan-out event stream a consumer may log wholesale.
    ///
    /// The identifier itself is `StableIdentifiers.processError`, the single place those names are
    /// spelled and the source `spec/identifiers.json` is generated from — never a second copy of that
    /// list, which nothing would keep equal to the first. This function stays as the name of the
    /// *decision* (an event reports the class, not the error), not as a second vocabulary.
    let failureKind (error: ProcessError) : string = StableIdentifiers.processError error

/// One typed transition of a live supervision, delivered by `SupervisionSession.EventsAsync` — the
/// stream counterpart of the `OnRestart`/`OnStormPause` callbacks and the `Status` snapshot, which it
/// adds to rather than replaces.
///
/// Read `Kind` first: it says which transition this is, and therefore which of the payload properties
/// below carry a value (each is `None` for every other kind). `Name` is the same fact as a stable
/// lowercase machine identifier — `incarnation_started`, `restart_scheduled`, … — suitable as a log
/// field or metric label, in the same `snake_case` style as the `"kind"` identifiers `ReportJson`
/// writes.
///
/// **Non-secret by construction.** An event carries lifecycle facts only: counters, a pid, an
/// `Outcome`, durations, the program name, and coarse failure/stop classifications. It never carries
/// argv, environment values, captured stdout/stderr, or a `ProcessError`'s message — the same taxonomy
/// `MemberInfo` and the library's logging already follow, so a consumer can forward the whole stream to
/// a log or metrics sink without auditing it for secrets.
///
/// Sealed with an internal constructor so it can gain fields without breaking the frozen API.
[<Sealed>]
type SupervisionEvent internal (payload: SupervisionEventPayload) =

    /// Which transition this event reports — the discriminator every consumer branches on.
    member _.Kind = payload.Kind

    /// This event's stable machine identifier (`incarnation_started`, `restart_scheduled`, …): the
    /// same information as `Kind`, in the lowercase form a structured log or metric label wants.
    /// Derived from `Kind` through the one function that spells these names, so the two can never
    /// disagree, and published in `spec/identifiers.json` for readers in other languages. Existing
    /// identifiers are never renamed; a new kind gets a new one.
    member _.Name = SupervisionEventPayload.eventName payload.Kind

    /// The supervised command's program name — on every event, so a stream merged across supervisors
    /// stays attributable.
    member _.Program = payload.Program

    /// The 1-based incarnation number this event is about (`IncarnationStarted`,
    /// `IncarnationFinished`, `IncarnationFailed`, `HealthCheckFailed`, `GaveUp`); `None` otherwise.
    /// The first incarnation is `1`, so it is one ahead of `SupervisionOutcome.Restarts`.
    member _.Attempt = payload.Attempt

    /// The live child's OS process id on `IncarnationStarted`; `None` on every other kind, and also on
    /// an `IncarnationStarted` from a runner that exposes no live handle (a capture-only test double).
    member _.Pid = payload.Pid

    /// How the incarnation ended, on `IncarnationFinished`; `None` otherwise.
    member _.Outcome = payload.Outcome

    /// How long the incarnation ran, on `IncarnationFinished`; `None` otherwise.
    member _.Duration = payload.Duration

    /// Whether the finished incarnation counts as a success under the command's `OkCodes`, on
    /// `IncarnationFinished`; `None` otherwise. A `false` here is what the `RestartPolicy` reads as a
    /// crash.
    member _.IsSuccess = payload.IsSuccess

    /// The failure's stable coarse class (`spawn`, `not_found`, `io`, …) on `IncarnationFailed` and
    /// `SupervisionFailed`; `None` otherwise. Intentionally a classification rather than the error
    /// itself — see the secret-safety note on this type.
    member _.FailureKind = payload.FailureKind

    /// The 1-based lifetime restart number, on `RestartScheduled`; `None` otherwise. Matches
    /// `SupervisionOutcome.Restarts` once that restart becomes the last one.
    member _.Restart = payload.Restart

    /// The delay about to be slept out — the jittered backoff on `RestartScheduled`, the jittered
    /// pause on `StormPaused`; `None` otherwise.
    member _.Delay = payload.Delay

    /// Why the restart is happening, on `RestartScheduled` (`RestartCause.Exit` or
    /// `RestartCause.Liveness`); `None` otherwise. The same value the `OnRestart` callback receives.
    member _.Cause = payload.Cause

    /// The 1-based lifetime storm-pause number, on `StormPaused`; `None` otherwise.
    member _.StormPause = payload.StormPause

    /// On `HealthCheckFailed`: `true` when the probe failed fatally and supervision is ending,
    /// `false` when the unhealthy streak tripped and the ordinary restart path takes over. `None` on
    /// every other kind.
    member _.IsTerminal = payload.IsTerminal

    /// Why supervision ended, on `Stopped`; `None` otherwise. The same `StopReason` the final
    /// `SupervisionOutcome` reports.
    member _.Reason = payload.Reason

    /// How many events were dropped in this gap, on `EventsDropped`; `None` otherwise. The running
    /// lifetime total is `SupervisionSession.DroppedEventCount`.
    member _.DroppedEvents = payload.DroppedEvents

    static member internal IncarnationStarted(program: string, attempt: int, pid: int option) =
        SupervisionEvent(
            { SupervisionEventPayload.create SupervisionEventKind.IncarnationStarted program with
                Attempt = Some attempt
                Pid = pid }
        )

    static member internal IncarnationFinished(program: string, attempt: int, result: ProcessResult<string>) =
        SupervisionEvent(
            { SupervisionEventPayload.create SupervisionEventKind.IncarnationFinished program with
                Attempt = Some attempt
                Outcome = Some result.Outcome
                Duration = Some result.Duration
                IsSuccess = Some result.IsSuccess }
        )

    static member internal IncarnationFailed(program: string, attempt: int, error: ProcessError) =
        SupervisionEvent(
            { SupervisionEventPayload.create SupervisionEventKind.IncarnationFailed program with
                Attempt = Some attempt
                FailureKind = Some(SupervisionEventPayload.failureKind error) }
        )

    static member internal RestartScheduled(program: string, restart: int, delay: TimeSpan, cause: RestartCause) =
        SupervisionEvent(
            { SupervisionEventPayload.create SupervisionEventKind.RestartScheduled program with
                Restart = Some restart
                Delay = Some delay
                Cause = Some cause }
        )

    static member internal StormPaused(program: string, stormPause: int, delay: TimeSpan) =
        SupervisionEvent(
            { SupervisionEventPayload.create SupervisionEventKind.StormPaused program with
                StormPause = Some stormPause
                Delay = Some delay }
        )

    static member internal HealthCheckFailed(program: string, attempt: int, isTerminal: bool) =
        SupervisionEvent(
            { SupervisionEventPayload.create SupervisionEventKind.HealthCheckFailed program with
                Attempt = Some attempt
                IsTerminal = Some isTerminal }
        )

    static member internal GaveUp(program: string, attempt: int) =
        SupervisionEvent(
            { SupervisionEventPayload.create SupervisionEventKind.GaveUp program with
                Attempt = Some attempt }
        )

    static member internal Stopped(program: string, reason: StopReason) =
        SupervisionEvent(
            { SupervisionEventPayload.create SupervisionEventKind.Stopped program with
                Reason = Some reason }
        )

    static member internal SupervisionFailed(program: string, error: ProcessError) =
        SupervisionEvent(
            { SupervisionEventPayload.create SupervisionEventKind.SupervisionFailed program with
                FailureKind = Some(SupervisionEventPayload.failureKind error) }
        )

    static member internal EventsDropped(program: string, dropped: int64) =
        SupervisionEvent(
            { SupervisionEventPayload.create SupervisionEventKind.EventsDropped program with
                DroppedEvents = Some dropped }
        )

/// One step of a consumer's read from a `SupervisionEventBuffer`, decided atomically under the owning
/// session's `gate` so a gap is always reported in exactly the position it happened.
[<RequireQualifiedAccess; NoComparison>]
type internal SupervisionEventStep =

    /// This many events were discarded before the ones still queued behind them.
    | Dropped of count: int64

    /// The oldest queued event.
    | Event of event: SupervisionEvent

    /// Nothing to take right now — the consumer waits on the channel instead.
    | Empty

/// The bounded buffer behind `SupervisionSession.EventsAsync`: a fixed-capacity channel with
/// ring-buffer (drop-oldest) semantics and an exact count of what it dropped.
///
/// **Not internally synchronized on purpose.** Every member here must be called with the owning
/// `SupervisionSession`'s `gate` held. That is the whole point: publication then runs under the same
/// single lock the session already uses for its stop/current-child/teardown state, so the supervision
/// loop, the per-incarnation liveness monitor, and the loop's own teardown can never interleave around
/// it — instead of this stream introducing a second, independent lock protocol beside that one (the
/// rule the `stopCts`/`sleepCts` `Cancel()`-vs-`Dispose()` race left behind).
///
/// Nothing here blocks or runs consumer code: the writes are `TryWrite`/`TryRead` only, and the
/// channel is built with `AllowSynchronousContinuations = false` so completing it (or filling it)
/// cannot resume a parked consumer inline while the loop holds `gate`.
type internal SupervisionEventBuffer(capacity: int) =

    // `SingleReader = false` is required, not a preference: the drop-oldest path evicts through
    // `Reader.TryRead()` from the writing side, so the channel must not assume one reader. `SingleWriter`
    // is left false for the same conservatism — writes are serialized by `gate`, but two different
    // threads (the loop and a liveness monitor) perform them. `FullMode.Wait` is what makes `TryWrite`
    // report "full" honestly; the channel's own Drop modes always claim success, which would hide the
    // very drops this buffer must count.
    let channel =
        Channel.CreateBounded<SupervisionEvent>(
            BoundedChannelOptions(
                capacity,
                SingleReader = false,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait,
                AllowSynchronousContinuations = false
            )
        )

    let mutable dropped = 0L
    let mutable pendingDropped = 0L
    let mutable completed = false

    member _.Reader: ChannelReader<SupervisionEvent> = channel.Reader

    /// Lifetime total of events discarded to make room for newer ones. Never reset.
    member _.DroppedCount = dropped

    /// The consumer's next step: an unreported gap first, then the oldest queued event, else nothing
    /// to read right now. Deciding both in ONE critical section is what makes the gap's position
    /// exact rather than approximate — a `Publish` cannot slip an eviction between "is there a gap?"
    /// and "take the next event", which would report the gap one event later than it happened.
    member _.TryTakeNext() : SupervisionEventStep =
        if pendingDropped > 0L then
            let gap = pendingDropped
            pendingDropped <- 0L
            SupervisionEventStep.Dropped gap
        else
            match channel.Reader.TryRead() with
            | true, event -> SupervisionEventStep.Event event
            | _ -> SupervisionEventStep.Empty

    /// Queue one event, evicting the oldest queued events when full (ring-buffer semantics: the newest
    /// lifecycle facts are the ones worth keeping) and counting every eviction. A no-op once the
    /// session has completed the stream.
    member _.Publish(event: SupervisionEvent) =
        if not completed then
            let mutable written = channel.Writer.TryWrite event
            let mutable stalled = false

            while not written && not stalled do
                let evicted, _ = channel.Reader.TryRead()

                if evicted then
                    dropped <- dropped + 1L
                    pendingDropped <- pendingDropped + 1L

                written <- channel.Writer.TryWrite event

                // The only writer is this `gate`-held path and the channel is not completed here, so a
                // freed slot cannot be stolen from us. Stopping when a failed write found nothing to
                // evict is therefore belt-and-braces against a livelock that would pin the supervision
                // loop itself — never the expected path.
                stalled <- not written && not evicted

    /// Close the stream so a consumer's enumeration ends once it has drained what is queued. Called
    /// exactly once, from the supervision loop's teardown.
    member _.Complete() =
        if not completed then
            completed <- true
            channel.Writer.TryComplete() |> ignore

/// The enumerator behind `SupervisionSession.EventsAsync`. Hand-written rather than
/// `ChannelReader.ReadAllAsync` for one reason: a drop gap has to be reported *in band*, immediately
/// before the oldest event that survived it, and that check belongs on the reading side — synthesizing
/// the marker into the channel itself would have to evict yet another event to make room for it.
type internal SupervisionEventEnumerator
    (
        reader: ChannelReader<SupervisionEvent>,
        takeNext: unit -> SupervisionEventStep,
        program: string,
        cancellationToken: CancellationToken
    ) =

    let mutable current = Unchecked.defaultof<SupervisionEvent>

    interface IAsyncEnumerator<SupervisionEvent> with
        member _.Current = current

        member _.MoveNextAsync() : ValueTask<bool> =
            ValueTask<bool>(
                task {
                    let mutable moved = false
                    let mutable finished = false

                    while not moved && not finished do
                        match takeNext () with
                        | SupervisionEventStep.Dropped count ->
                            current <- SupervisionEvent.EventsDropped(program, count)
                            moved <- true
                        | SupervisionEventStep.Event event ->
                            current <- event
                            moved <- true
                        | SupervisionEventStep.Empty ->
                            // Waiting happens OUTSIDE the session's lock, on the channel itself. It
                            // resolves `false` only once the session has completed the stream and it is
                            // drained — the natural end of supervision, not an error.
                            let! more = reader.WaitToReadAsync cancellationToken
                            finished <- not more

                    return moved
                }
            )

        member _.DisposeAsync() = ValueTask.CompletedTask

/// The `IAsyncEnumerable<SupervisionEvent>` that `SupervisionSession.EventsAsync` returns.
type internal SupervisionEventStream
    (reader: ChannelReader<SupervisionEvent>, takeNext: unit -> SupervisionEventStep, program: string) =

    interface IAsyncEnumerable<SupervisionEvent> with
        member _.GetAsyncEnumerator(cancellationToken: CancellationToken) : IAsyncEnumerator<SupervisionEvent> =
            SupervisionEventEnumerator(reader, takeNext, program, cancellationToken)

[<Sealed>]
type private CaptureOnlyStopLever(source: CancellationTokenSource, startTime: DateTime, startedTimestamp: int64) =
    let identity = obj ()
    let mutable stopRequested = false

    member _.Identity = identity
    member _.Source = source
    member _.StartTime = startTime
    member _.StartedTimestamp = startedTimestamp

    member _.StopRequested
        with get () = stopRequested
        and set value = stopRequested <- value

/// A live handle to a running supervision, returned by `Supervisor.StartAsync`. Unlike `RunAsync` —
/// which only reports its `SupervisionOutcome` at the very end — a session lets a caller watch
/// supervision *while it runs* (`Status`), ask it to stop *gracefully* (`StopAsync`), and await its
/// eventual outcome (`Completion`). This is the primitive for building daemons / process managers on
/// top of the runner layer without pulling in `Microsoft.Extensions.Hosting`.
///
/// Thread-safe: `Status` is read under the same lock the supervision loop uses to publish each state
/// change, so a concurrent read never races an update nor throws; `StopAsync` is idempotent and
/// race-safe against the loop (and against a repeat call).
///
/// Sealed with an internal constructor — build one via `Supervisor.StartAsync`.
[<Sealed>]
type SupervisionSession internal (config: SupervisorConfig, cancellationToken: CancellationToken) =

    // Serializes the observable snapshot and the stop/current-child state, so an external `Status`/
    // `StopAsync` reader never races the supervision loop's publications. Kept deliberately simple (one
    // lock, immutable snapshot) rather than a lattice of volatiles — the loop publishes rarely (once per
    // incarnation / restart / storm pause) and the lock is uncontended on the hot path.
    let gate = obj ()

    // Cancels an in-flight backoff / storm-pause sleep when a graceful stop is requested, so a stop
    // taken *between* incarnations ends the loop promptly instead of waiting the delay out. Distinct
    // from the caller's `cancellationToken` (whose cancellation is an *error*): a stop is not an error.
    let stopCts = new CancellationTokenSource()

    // The opt-in live event stream's bounded buffer (`Supervisor.Events`), or `None` — the default —
    // when no stream was configured, in which case `emit` below allocates neither a channel nor a single
    // event. Built here, in the constructor, BEFORE the loop is launched: a consumer that calls
    // `EventsAsync` after `StartAsync` has returned still sees the first incarnation's events, because
    // the buffer was already retaining them. Its members are called only under `gate` (see the type).
    let eventBuffer = config.EventCapacity |> Option.map SupervisionEventBuffer

    let eventProgram = config.Command.Program

    // Latches when `EventsAsync` hands the stream out: reading a channel is destructive, so a second
    // consumer would silently steal events from the first. One consumer, refused loudly after that.
    let mutable eventsTaken = false

    // Loop-owned mirror fields (only the supervision loop writes them), republished into `status` under
    // `gate` on every change so external readers see a consistent snapshot.
    let mutable restarts = 0
    let mutable stormPaused = false
    let mutable active = true
    let mutable current: RunningProcess option = None
    let mutable captureOnlyCurrent: CaptureOnlyStopLever option = None

    // The graceful-stop request, set by `StopAsync`, read by the loop and by `captureIncarnation`.
    let mutable stopping = false
    let mutable stopGrace = TimeSpan.Zero

    // Latches false when the configured runner explicitly proves capture-only by throwing
    // `NotSupportedException` synchronously from `SpawnAsync`: the session then drives incarnations
    // through `CaptureStringAsync` — no live pid / graceful child-stop, but supervision itself is
    // unaffected. Any other exception remains a runner defect and escapes instead of being masked.
    let mutable spawnCapable = true

    // Set by a per-incarnation liveness monitor the moment it decides the live child is unresponsive
    // (before it gracefully stops it); read-and-reset once per incarnation by the loop, so the restart
    // that follows is reported with `RestartCause.Liveness`. Guarded by `gate` for cross-thread
    // visibility (the monitor runs on a background task, the loop on another). At most one write per
    // incarnation, always ordered before the graceful stop that makes the loop observe the exit.
    let mutable livenessTripped = false

    // A resource probe can fail because the active containment backend cannot provide attributable
    // whole-tree accounting. Preserve that typed failure across the graceful stop it triggers.
    let mutable livenessFatalError: ProcessError option = None

    // The atomically-published snapshot. Seeded active-with-no-child; refreshed on every state change.
    let mutable status = SupervisionStatus(true, 0, false, None, None)

    // Rebuild the published snapshot from the mirror fields. Caller must hold `gate`.
    let refresh () =
        let pid = current |> Option.bind (fun running -> running.Pid)

        let startTime =
            match current, captureOnlyCurrent with
            | Some running, _ -> Some running.StartTime
            | None, Some lever -> Some lever.StartTime
            | None, None -> None

        status <- SupervisionStatus(active, restarts, stormPaused, pid, startTime)

    // Publish a freshly-spawned child as the current incarnation AND, atomically under `gate`, learn
    // whether a graceful stop is already pending — closing the `StopAsync`-vs-spawn race: whichever of
    // the two takes `gate` second sees the other's write, so the child is always stopped exactly once
    // (here if the stop landed first, or in `StopAsync` if the publish landed first).
    let publishCurrent (running: RunningProcess) : TimeSpan option =
        lock gate (fun () ->
            current <- Some running
            refresh ()
            if stopping then Some stopGrace else None)

    let clearCurrent (running: RunningProcess) =
        lock gate (fun () ->
            match current with
            | Some existing when Object.ReferenceEquals(existing, running) -> current <- None
            | _ -> ()

            refresh ())

    let cancelCaptureOnly (lever: CaptureOnlyStopLever) =
        try
            lever.Source.Cancel()
        with
        | :? ObjectDisposedException ->
            // The same incarnation completed and disposed its lever after it was snapshotted; there is
            // no active capture left to interrupt.
            ()
        | :? AggregateException ->
            // Cancellation callbacks belong to the injected runner. A faulty callback must not prevent
            // the supervisor from completing its own deliberate-stop protocol.
            ()

    // Publish the capture token and its observable start time as one state transition. If a stop won the
    // gate first, mark the lever under the gate and cancel it after releasing the gate; otherwise
    // `requestGracefulStop` does the same. Thus the capability-probe gap cannot leave an uninterruptible
    // capture behind without invoking runner-owned cancellation callbacks while holding `gate`.
    let publishCaptureOnly (lever: CaptureOnlyStopLever) =
        let shouldCancel =
            lock gate (fun () ->
                captureOnlyCurrent <- Some lever
                refresh ()

                if stopping then
                    lever.StopRequested <- true
                    true
                else
                    false)

        if shouldCancel then
            cancelCaptureOnly lever

    let captureStopWasRequested (lever: CaptureOnlyStopLever) =
        lock gate (fun () -> lever.StopRequested)

    let clearCaptureOnly (lever: CaptureOnlyStopLever) =
        lock gate (fun () ->
            match captureOnlyCurrent with
            | Some existing when Object.ReferenceEquals(existing.Identity, lever.Identity) ->
                captureOnlyCurrent <- None
                refresh ()
            | _ -> ()

            lever.Source.Dispose())

    let bumpRestarts () =
        lock gate (fun () ->
            restarts <- restarts + 1
            refresh ())

    let setStormPaused (value: bool) =
        lock gate (fun () ->
            stormPaused <- value
            refresh ())

    let markInactive () =
        lock gate (fun () ->
            active <- false
            current <- None
            captureOnlyCurrent <- None
            refresh ())

    let isStopping () = lock gate (fun () -> stopping)

    // Publish one lifecycle transition to the opt-in event stream. `build` is a thunk, so a session
    // without a configured stream — the default, and every `RunAsync` — never even allocates the event:
    // supervision behaves exactly as it did before this stream existed.
    //
    // The queue write runs under the SAME `gate` as every other session state change rather than behind
    // a lock of its own. That is what keeps the loop's own emissions, the per-incarnation liveness
    // monitor's, and the loop's teardown (which completes the buffer beside the `stopCts`/`sleepCts`
    // disposal, under this same lock) strictly serialized — the discipline the earlier
    // `Cancel()`-vs-`Dispose()` race established for this session. The event itself is built OUTSIDE the
    // lock, and the buffer only does non-blocking `TryWrite`/`TryRead`, so `gate` is never held across
    // anything that could block or run consumer code.
    let emit (build: unit -> SupervisionEvent) =
        match eventBuffer with
        | None -> ()
        | Some buffer ->
            let event = build ()
            lock gate (fun () -> buffer.Publish event)

    // Record a graceful-stop request and snapshot the current live child atomically under `gate` (closing
    // the `StopAsync`-vs-spawn race, see `publishCurrent`). A capture-only incarnation has no graceful
    // process handle, so cancel its identity-scoped lever immediately. Then interrupt any in-flight
    // backoff / storm sleep so a stop taken *between* incarnations ends the loop promptly instead of
    // waiting the delay out. Shared by the public `StopAsync` and the internal `StopActiveAsync` seam.
    let requestGracefulStop (gracePeriod: TimeSpan) : RunningProcess option =
        let child, captureOnly =
            lock gate (fun () ->
                stopGrace <- gracePeriod
                stopping <- true

                captureOnlyCurrent |> Option.iter (fun lever -> lever.StopRequested <- true)

                current, captureOnlyCurrent)

        try
            captureOnly |> Option.iter cancelCaptureOnly
        finally
            // `stopCts.Cancel()` is serialized against `runLoop`'s teardown `Dispose()` under `gate` (see
            // its `finally`), so the two can never interleave. Keep this in `finally` so a runner-owned
            // capture cancellation callback cannot prevent an in-flight backoff / storm pause from being
            // interrupted.
            lock gate (fun () ->
                try
                    stopCts.Cancel()
                with
                | :? ObjectDisposedException ->
                    // Already disposed by a concurrent `runLoop` teardown; the loop has already ended and
                    // there is nothing further to cancel.
                    ()
                | :? AggregateException ->
                    // Linked sleep callbacks have already been notified. A faulty callback must not turn
                    // a deliberate stop into an exception after the stop signal has been delivered.
                    ())

        child

    // Observe an abandoned graceful-stop task's eventual fault so it never surfaces as an unobserved
    // task exception at finalization — shared by `StopActiveAsync` (the hosting stop seam) and the
    // per-incarnation liveness monitor, mirroring `RunningProcess`'s own abandoned-stop fault observation.
    let observeFault (stopTask: Task<Outcome>) =
        stopTask.ContinueWith(
            Action<Task<Outcome>>(fun completed -> completed.Exception |> ignore),
            TaskContinuationOptions.OnlyOnFaulted
            ||| TaskContinuationOptions.ExecuteSynchronously
        )
        |> ignore

    let markLivenessTripped () =
        lock gate (fun () -> livenessTripped <- true)

    // Read the liveness flag and reset it in the same critical section, so each incarnation observes at
    // most its own monitor's verdict and none leaks into the next incarnation.
    let takeLivenessTripped () =
        lock gate (fun () ->
            let tripped = livenessTripped
            livenessTripped <- false
            tripped)

    let markLivenessFatalError error =
        lock gate (fun () -> livenessFatalError <- Some error)

    let takeLivenessFatalError () =
        lock gate (fun () ->
            let error = livenessFatalError
            livenessFatalError <- None
            error)

    // A per-incarnation liveness monitor: while the live child runs, periodically ask a configured probe
    // whether it is still healthy, and after `LivenessFailures` consecutive failed attempts, gracefully
    // stop it so `captureIncarnation`'s output verb returns and the ORDINARY restart path (policy +
    // backoff) takes over — never a second, parallel restart mechanism. A no-op (an already-completed
    // task) when no liveness probe is configured.
    //
    // Started as a `backgroundTask` (KB K-009): the monitor is a fresh async loop that could, in a future
    // caller shape, be blocked on synchronously; keeping it off any captured `SynchronizationContext`
    // guarantees it never deadlocks a single-threaded UI/ASP.NET host — the same reasoning the `runLoop`
    // itself is a `backgroundTask` for. Off any such context (tests, CI) `backgroundTask` is identical to
    // `task`.
    //
    // Each attempt reuses the shared readiness poll/deadline core (`ReadinessProbe.waitForCoreUsing`, via
    // its `waitForHttpUsing`/`waitFor` funnels — KB K-043) rather than re-implementing polling/deadline
    // logic: `Ok ()` = healthy (reset the failure run), `NotReady` = this attempt failed, `Cancelled` =
    // the incarnation ended / the monitor was torn down. The probe is handed `None`/`None` for the child's
    // pipes: those belong to the incarnation's own `OutputStringAsync`, so the liveness probe only touches
    // the external endpoint/predicate and never a second reader on the child (KB K-016/K-031 untouched).
    // `attemptNumber` is the incarnation this monitor belongs to — spelled out rather than `attempt`,
    // which below names the per-tick health check itself.
    let monitorLiveness
        (running: RunningProcess)
        (attemptNumber: int)
        (incarnation: Task<Result<ProcessResult<string>, ProcessError>>)
        (token: CancellationToken)
        : Task =
        match config.Liveness with
        | None -> Task.CompletedTask
        | Some probe ->
            let program = config.Command.Program
            let probeTimeout = config.LivenessTimeout
            let interval = Liveness.clampInterval config.LivenessInterval
            let threshold = max 1 config.LivenessFailures
            let grace = config.LivenessGrace
            let livenessDelay = config.LivenessDelay

            // Build the per-attempt health check and whatever it owns. An HTTP monitor holds ONE
            // `HttpClient` for its whole lifetime and reuses it across attempts (a periodic probe must not
            // churn a client/socket per tick); a predicate monitor owns nothing (a no-op disposable). Both
            // feed `waitForCoreUsing` through an existing funnel, so there is no fifth copy of the poll/
            // deadline logic.
            let attempt, resources =
                match probe with
                | LivenessProbe.Http(uri, isSatisfactory, callerClient) ->
                    let client, resources =
                        match callerClient with
                        | Some client -> client, None
                        | None ->
                            let client = new HttpClient(Timeout = Timeout.InfiniteTimeSpan)
                            client, Some(client :> IDisposable)

                    let check (probeToken: CancellationToken) =
                        ReadinessProbe.waitForHttpUsing
                            config.Command.Config.TimeProvider
                            (fun requestUri ct -> client.GetAsync(requestUri, ct))
                            isSatisfactory
                            program
                            uri
                            ReadinessAttempts.PollUntilDeadline
                            probeTimeout
                            probeToken

                    check, resources
                | LivenessProbe.Custom userProbe ->
                    let check (probeToken: CancellationToken) =
                        ReadinessProbe.waitFor
                            config.Command.Config.TimeProvider
                            program
                            None
                            None
                            (Func<Task<bool>> userProbe)
                            ReadinessAttempts.PollUntilDeadline
                            probeTimeout
                            probeToken

                    check, None
                | LivenessProbe.Memory maxBytes ->
                    let check (_probeToken: CancellationToken) =
                        // ProcessGroupStats.PeakMemoryBytes is monotonic for a private Job/cgroup
                        // incarnation. A later lower working set therefore cannot forgive a peak that
                        // already crossed the limit; this is the explicit memory-liveness contract.
                        match running.TreePeakMemoryBytes() with
                        | Ok bytes when bytes <= maxBytes -> Task.FromResult(Ok())
                        | Ok _ -> Task.FromResult(Error(ProcessError.NotReady(program, probeTimeout)))
                        | Error error -> Task.FromResult(Error error)

                    check, None

            backgroundTask {
                try
                    let mutable consecutiveFailures = 0
                    let mutable tripped = false

                    while not tripped && not token.IsCancellationRequested && not incarnation.IsCompleted do
                        let mutable waited = true

                        try
                            do! livenessDelay interval token
                        with :? OperationCanceledException ->
                            // Torn down (incarnation ended, or the session is stopping) during the gap
                            // between attempts; the loop guard ends the monitor.
                            waited <- false

                        if waited && not token.IsCancellationRequested && not incarnation.IsCompleted then
                            let! outcome =
                                task {
                                    try
                                        return! attempt token
                                    with ex ->
                                        // A liveness probe must never fault the monitor (which would leave
                                        // the child unsupervised): treat any unexpected fault as a failed
                                        // attempt. `waitForCoreUsing`/`waitForHttpUsing` already swallow the
                                        // expected network/cancellation failures; this guards the rest.
                                        return Error(ProcessError.Io ex.Message)
                                }

                            match outcome with
                            | Ok() ->
                                // Healthy: any prior failure run is forgiven (only CONSECUTIVE failures trip).
                                consecutiveFailures <- 0
                            | Error(ProcessError.Cancelled _) ->
                                // Torn down mid-attempt; not a health failure. The loop guard ends the monitor.
                                ()
                            | Error error ->
                                let fatalResourceError =
                                    match probe, error with
                                    | LivenessProbe.Memory _, ProcessError.NotReady _ -> false
                                    | LivenessProbe.Memory _, _ -> true
                                    | _ -> false

                                if fatalResourceError then
                                    tripped <- true
                                    markLivenessFatalError error
                                    // The probe itself failed fatally: supervision ends rather than
                                    // restarting, so the event is reported as terminal.
                                    emit (fun () ->
                                        SupervisionEvent.HealthCheckFailed(eventProgram, attemptNumber, true))

                                    observeFault (running.StopAsync grace)
                                else
                                    consecutiveFailures <- consecutiveFailures + 1

                                if not fatalResourceError && consecutiveFailures >= threshold then
                                    tripped <- true
                                    // Record the liveness verdict BEFORE stopping the child, so the loop
                                    // observes the flag once the graceful stop makes `OutputStringAsync`
                                    // return (the write is ordered before the stop under `gate`).
                                    markLivenessTripped ()

                                    // The unhealthy streak tripped: not terminal, the ordinary policy /
                                    // backoff path decides what happens next.
                                    emit (fun () ->
                                        SupervisionEvent.HealthCheckFailed(eventProgram, attemptNumber, false))

                                    // Gracefully stop the live child through its own path; fire-and-forget
                                    // with fault observation, exactly like the pending-graceful-stop path in
                                    // `captureIncarnation`. Its exit makes the incarnation's output verb
                                    // return, and the ordinary restart path takes over.
                                    observeFault (running.StopAsync grace)
                finally
                    resources |> Option.iter (fun resource -> resource.Dispose())
            }
            :> Task

    // Drive one incarnation to a completion result. Prefer a spawn+track path so the session can expose
    // the live child's pid/StartTime and stop it through `RunningProcess.StopAsync`; a capture-only
    // runner (a scripted double with no live handle) latches onto its `CaptureStringAsync` primitive.
    // The tracked path is a faithful inline of `CaptureVerbs.runToCompletion` (kept in step with it) —
    // same `CancelOn` linking, same up-front and post-consume cancellation checks — plus the live-handle
    // publication and the capture-only fallback.
    let captureOnlyIncarnation (attempt: int) (command: Command) : Task<Result<ProcessResult<string>, ProcessError>> =
        task {
            let captureCts = CancellationTokenSource.CreateLinkedTokenSource cancellationToken

            let lever =
                CaptureOnlyStopLever(
                    captureCts,
                    command.Config.TimeProvider.GetUtcNow().UtcDateTime,
                    Stopwatch.GetTimestamp()
                )

            publishCaptureOnly lever

            // Same transition as the live path's, reported on the same terms: a capture-only runner has
            // no live handle, so the incarnation is "started" when its capture begins and carries no pid.
            emit (fun () -> SupervisionEvent.IncarnationStarted(eventProgram, attempt, None))

            try
                try
                    let! captured = config.Runner.CaptureStringAsync(command, captureCts.Token)

                    match captured with
                    | Error(ProcessError.Cancelled _) when captureStopWasRequested lever ->
                        return
                            Ok(
                                ProcessResult<string>(
                                    command.Program,
                                    "",
                                    "",
                                    Outcome.Unobserved
                                        "Capture-only incarnation stopped before its exit status was observed.",
                                    Stopwatch.GetElapsedTime lever.StartedTimestamp,
                                    false,
                                    command.Config.OkCodes
                                )
                            )
                    | result -> return result
                with :? OperationCanceledException when captureCts.IsCancellationRequested ->
                    if captureStopWasRequested lever then
                        return
                            Ok(
                                ProcessResult<string>(
                                    command.Program,
                                    "",
                                    "",
                                    Outcome.Unobserved
                                        "Capture-only incarnation stopped before its exit status was observed.",
                                    Stopwatch.GetElapsedTime lever.StartedTimestamp,
                                    false,
                                    command.Config.OkCodes
                                )
                            )
                    else
                        return Error(ProcessError.Cancelled command.Program)
            finally
                clearCaptureOnly lever
        }

    let captureIncarnation (attempt: int) (command: Command) : Task<Result<ProcessResult<string>, ProcessError>> =
        if not spawnCapable then
            captureOnlyIncarnation attempt command
        else
            task {
                use linkedCts =
                    match command.Config.CancelOn with
                    | Some extra -> CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, extra)
                    | None -> CancellationTokenSource.CreateLinkedTokenSource cancellationToken

                let effectiveToken = linkedCts.Token

                if effectiveToken.IsCancellationRequested then
                    return Error(ProcessError.Cancelled command.Program)
                else
                    let spawned =
                        try
                            Some(config.Runner.SpawnAsync(command, effectiveToken))
                        with
                        // This is the documented capability marker for a capture-only runner: it has no
                        // live handle to return, so falling back is intended. Other exceptions are not
                        // caught; masking a runner bug would silently remove status, stop, and liveness.
                        | :? NotSupportedException ->
                            spawnCapable <- false
                            None

                    match spawned with
                    | None -> return! captureOnlyIncarnation attempt command
                    | Some spawnTask ->
                        match! spawnTask with
                        | Error error -> return Error error
                        | Ok running ->
                            let pendingGrace = publishCurrent running

                            // Published only for a child that really started: a spawn that failed above
                            // never reaches here and is reported by the loop as `IncarnationFailed`.
                            emit (fun () -> SupervisionEvent.IncarnationStarted(eventProgram, attempt, running.Pid))

                            // Cancelling a supervised incarnation goes through the run's own single
                            // cancellation seam, exactly as `CaptureVerbs.runToCompletion` does (this path
                            // is a faithful inline of it): the unchanged immediate hard kill by default,
                            // and the `Command.CancelSignal` -> `CancelGrace` -> hard-kill ladder when the
                            // supervised command opted in. Independent of `SupervisorOptions`' own stop
                            // grace, which governs a REQUESTED graceful stop (`StopAsync`), not a
                            // cancellation — the two are different events and neither gap-fills the other.
                            use _registration = effectiveToken.Register(fun () -> running.BeginCancelTeardown())

                            // A graceful stop landed just before this child became current: stop it now
                            // through its own path (fire-and-forget — `OutputStringAsync` below observes
                            // the exit, and the loop ends with `Stopped` once this returns).
                            match pendingGrace with
                            | Some grace -> observeFault (running.StopAsync grace)
                            | None -> ()

                            // Drive the incarnation's output verb while a per-incarnation liveness monitor
                            // watches the live child (a no-op when no liveness probe is configured). If the
                            // monitor decides the child is unresponsive it gracefully stops it, which makes
                            // `OutputStringAsync` return so the ordinary restart path takes over. The monitor
                            // is scoped to exactly this incarnation: cancelled and awaited once the output
                            // verb returns, whatever its outcome. A task CE cannot `do!` inside `finally`, so
                            // this uses the established capture-fault-then-single-cleanup shape (see
                            // `ReadinessProbe.withBackgroundDrain`): capture any fault, always tear the
                            // monitor down and clear the current child, then re-raise the captured fault.
                            use livenessCts = CancellationTokenSource.CreateLinkedTokenSource effectiveToken

                            let outputTask = running.OutputStringAsync()
                            let monitorTask = monitorLiveness running attempt outputTask livenessCts.Token

                            let mutable captured =
                                Unchecked.defaultof<Result<ProcessResult<string>, ProcessError>>

                            let mutable fault: exn option = None

                            try
                                let! result = outputTask

                                captured <-
                                    if effectiveToken.IsCancellationRequested then
                                        Error(ProcessError.Cancelled command.Program)
                                    else
                                        result
                            with ex ->
                                fault <- Some ex

                            livenessCts.Cancel()
                            do! monitorTask
                            clearCurrent running

                            let fatalLivenessError = takeLivenessFatalError ()

                            match fatalLivenessError, fault with
                            | Some error, _ -> return Error error
                            | None, Some(:? ProcessException as ex) ->
                                // Buffered pump failures are typed ProcessExceptions at the live-handle
                                // boundary; supervision consumes the structured error so transient I/O can
                                // follow the configured restart policy and retain liveness attribution.
                                return Error ex.Error
                            | None, Some ex ->
                                return! Task.FromException<Result<ProcessResult<string>, ProcessError>> ex
                            | None, None -> return captured
            }

    // The supervision loop itself — one faithful copy of `Supervisor.RunAsync`'s former body, extended
    // with the session's live-status publication and graceful-stop handling. Started in the background
    // by the constructor (`let completion` below); `RunAsync` awaits its result through `Completion`.
    //
    // Runs as a `backgroundTask` — detached onto the thread pool — so it never captures the
    // `SynchronizationContext` of the thread that called `StartAsync`. The loop is kicked off
    // synchronously from the `SupervisionSession` constructor (itself built synchronously by
    // `Supervisor.StartAsync`), and its `Completion` is exactly the primitive a daemon/process-manager
    // consumer naturally blocks on (`Completion.GetAwaiter().GetResult()`, `StopAsync(grace).Result`).
    // A plain `task { }` would post every post-`await` continuation (each `config.Sleep`,
    // `captureIncarnation`, `OutputStringAsync`) back to the caller's context; on a single-threaded
    // context (a WPF/WinForms UI thread, classic ASP.NET) that blocking wait would deadlock the loop —
    // the one thread is parked in the wait, so no continuation could ever run. `backgroundTask` keeps
    // the whole loop on the pool, so such a blocking wait is safe (see `Pump.feedStdin` for the same
    // pattern). Off any such context — a pool or background thread, as in the tests and CI —
    // `backgroundTask` is identical to `task`, so nothing else changes.
    let runLoop () : Task<Result<SupervisionOutcome, ProcessError>> =
        let factor =
            if Double.IsFinite config.BackoffFactor then
                max config.BackoffFactor 1.0
            else
                1.0

        let command = config.Command.OutputBuffer config.Capture
        let program = config.Command.Program

        // User callbacks are part of the supervision decision path, but they are not allowed to
        // escape the Result-returning supervision contract. Keep the source context in the typed
        // error: a callback fault may happen after a result/error has already been produced, and
        // losing that context makes the terminal failure needlessly opaque.
        //
        // That context is kept in full on the typed error - `ProcessError.Io`'s `Detail` field carries
        // this whole composite string, however long it grows. Only the one-line RENDER of it
        // (`ProcessError.Message`, which previews each fragment up to `MessageText.MaxFragmentChars`)
        // is bounded, and it is bounded from the END - hence the order below: which callback failed,
        // then what it threw, then the run it failed around, whose own tail is already a bounded
        // preview of the child's output. A detail long enough to be cut therefore shortens that
        // trailing context first, and a callback whose own exception message fills the budget can
        // crowd the context out of the render entirely; `Detail` still has all of it.
        let callbackFailure (callbackName: string) (context: string) (error: exn) : ProcessError =
            let exceptionDetail =
                if String.IsNullOrWhiteSpace error.Message then
                    error.GetType().Name
                else
                    $"{error.GetType().Name}: {error.Message}"

            ProcessError.Io $"Supervisor callback '{callbackName}' failed: {exceptionDetail}; {context}"

        let resultContext (result: ProcessResult<string>) =
            let verdict =
                if result.IsSuccess then
                    let code = result.Code |> Option.map string |> Option.defaultValue "unknown"
                    $"completed successfully (code {code})"
                else
                    $"failed with {result.FailureError.Message}"

            $"result context for '{result.Program}': {verdict}"

        let errorContext (error: ProcessError) =
            $"error context for '{program}': {error.Message}"

        let invokeCallback (callbackName: string) (context: string) (callback: unit -> unit) =
            try
                callback ()
                Ok()
            with error ->
                Error(callbackFailure callbackName context error)

        let stopWhenMatches (result: ProcessResult<string>) : Result<bool, ProcessError> =
            match config.StopWhen with
            | None -> Ok false
            | Some predicate ->
                try
                    Ok(predicate result)
                with error ->
                    Error(callbackFailure "StopWhen" (resultContext result) error)

        let giveUpMatches (error: ProcessError) : Result<bool, ProcessError> =
            match config.GiveUpWhen with
            | None -> Ok false
            | Some classify ->
                try
                    Ok(classify error)
                with callbackError ->
                    Error(callbackFailure "GiveUpWhen" (errorContext error) callbackError)

        let restartCapable =
            match config.Policy with
            | RestartPolicy.Never -> false
            | _ -> config.MaxRestarts |> Option.forall (fun limit -> limit > 0)

        backgroundTask {
            // Force the async boundary before any real work, so the constructor returns before the first
            // incarnation is spawned (the whole configure-and-spawn prefix runs off the caller's thread).
            do! Task.Yield()

            // Sleeps observe both the caller's cancellation and a session stop, so a graceful stop (which
            // cancels `stopCts`) promptly interrupts an in-flight backoff / storm pause. A live-handle
            // incarnation keeps the caller's token only and is stopped through `RunningProcess.StopAsync`;
            // a capture-only incarnation gets its own separately published cancellation lever.
            use sleepCts =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stopCts.Token)

            let sleepToken = sleepCts.Token

            let mutable escalation = 0
            let mutable stormScore = 0.0
            let mutable lastFailureAt: float option = None
            let mutable stormPauses = 0
            let mutable lastResult: ProcessResult<string> option = None

            // The terminal error of the last incarnation that produced no `ProcessResult` at all (a
            // transient spawn/IO failure the policy chose to retry). Kept so a graceful stop taken while
            // backing off after such a failure still reports the honest reason the child never came up,
            // exactly as the post-capture `Error` branch below does — instead of replacing it with a
            // synthetic one. Only ever consulted while `lastResult` is still `None`.
            let mutable lastError: ProcessError option = None

            let mutable final: Result<SupervisionOutcome, ProcessError> option =
                if restartCapable && Stdin.isOneShot command.Config.StdinSource then
                    Some(
                        Error(
                            ProcessError.Unsupported
                                $"'{program}' has a one-shot stdin source and cannot be supervised with restarts enabled: a restarted incarnation would find the source already exhausted"
                        )
                    )
                else
                    None

            let stormGate (context: string) : Task<Result<unit, ProcessError>> =
                task {
                    match config.StormPause with
                    | None -> return Ok()
                    | Some pause ->
                        let now = config.Now()

                        let elapsed =
                            match lastFailureAt with
                            | Some at -> max 0.0 (now - at)
                            | None -> 0.0

                        lastFailureAt <- Some now
                        stormScore <- Supervision.decayedFailureScore stormScore elapsed config.FailureDecay

                        if Double.IsFinite config.FailureThreshold && stormScore > config.FailureThreshold then
                            let jittered = Supervision.applyJitter pause config.Jitter
                            Log.stormPause config.Command.Config.Logger program jittered
                            Diag.stormPaused program

                            // Reported beside the existing log/metric and BEFORE the user callback, so the
                            // stream sees the pause even when a throwing `OnStormPause` ends supervision.
                            let pauseNumber = stormPauses + 1
                            emit (fun () -> SupervisionEvent.StormPaused(program, pauseNumber, jittered))

                            let mutable callbackError: ProcessError option = None

                            let callbackResult =
                                match config.OnStormPause with
                                | Some handler ->
                                    invokeCallback "OnStormPause" context (fun () ->
                                        handler (SupervisorStormPauseEvent(program, stormPauses + 1, jittered)))
                                | None -> Ok()

                            match callbackResult with
                            | Error error -> callbackError <- Some error
                            | Ok() ->
                                // Bracket exactly the pause window in the live status: paused while the jittered
                                // sleep runs, cleared the instant it returns (or is cut short by a stop).
                                setStormPaused true

                                if jittered > TimeSpan.Zero then
                                    do! config.Sleep jittered sleepToken

                                setStormPaused false
                                stormScore <- 0.0
                                lastFailureAt <- None
                                stormPauses <- stormPauses + 1

                            match callbackError with
                            | Some error -> return Error error
                            | None -> return Ok()
                        else
                            return Ok()
                }

            let sleepBackoff
                (exponent: int)
                (restartNumber: int)
                (cause: RestartCause)
                (context: string)
                : Task<Result<unit, ProcessError>> =
                task {
                    let delay =
                        Supervision.backoffDelay config.BackoffBase factor exponent config.MaxBackoff

                    let delay = Supervision.applyJitter delay config.Jitter
                    Log.supervisorRestart config.Command.Config.Logger program restartNumber delay
                    Diag.supervisorRestarted program

                    // A liveness-forced restart is additionally surfaced under its own event/metric, so an
                    // operator can tell a live-child health restart apart from an ordinary crash
                    // restart without inventing a parallel event system — same `ProcessKitDiagnostics`
                    // taxonomy, one extra id. The generic restart telemetry above still fires (it IS a
                    // restart and counts in `SupervisionOutcome.Restarts`).
                    match cause with
                    | RestartCause.Liveness ->
                        Log.supervisorLivenessRestart config.Command.Config.Logger program config.LivenessFailures
                        Diag.supervisorLivenessRestarted program
                    | RestartCause.Exit -> ()

                    // Reported beside the existing log/metric and BEFORE the user callback, so the stream
                    // sees the scheduled restart even when a throwing `OnRestart` ends supervision.
                    emit (fun () -> SupervisionEvent.RestartScheduled(program, restartNumber, delay, cause))

                    let callbackResult =
                        match config.OnRestart with
                        | Some handler ->
                            invokeCallback "OnRestart" context (fun () ->
                                handler (SupervisorRestartEvent(program, restartNumber, delay, cause)))
                        | None -> Ok()

                    let mutable callbackError: ProcessError option = None

                    match callbackResult with
                    | Error error -> callbackError <- Some error
                    | Ok() ->
                        if delay > TimeSpan.Zero then
                            do! config.Sleep delay sleepToken

                    match callbackError with
                    | Some error -> return Error error
                    | None -> return Ok()
                }

            let budgetExhausted () =
                config.MaxRestarts |> Option.exists (fun limit -> restarts >= limit)

            // The 1-based incarnation number every event of an incarnation is tagged with. Loop-local and
            // loop-owned: it is bumped once per attempted incarnation (including one whose spawn fails, so
            // an `IncarnationFailed` is still attributable) and handed down explicitly to the incarnation
            // and its liveness monitor, rather than being re-derived from `restarts` — which only advances
            // after a backoff, and would therefore misattribute events on the paths that never sleep.
            let mutable attempt = 0

            try
                while final.IsNone do
                    if cancellationToken.IsCancellationRequested then
                        final <- Some(Error(ProcessError.Cancelled program))
                    elif isStopping () then
                        // A graceful stop is pending and no child is live right now — it was requested
                        // before the first incarnation was ever spawned, or it interrupted the backoff /
                        // storm sleep between two of them. Deciding HERE, *before* the spawn, is what
                        // actually keeps a stop from launching one more child only to stop it again (and,
                        // on a capture-only runner, from running a whole extra incarnation to completion
                        // with no stop path at all). Checked independently of `lastResult`, because a
                        // supervision that has not produced a result yet is exactly the case that used to
                        // fall through to another incarnation.
                        match lastResult, lastError with
                        | Some last, _ ->
                            // At least one incarnation completed: report its honest result under
                            // `Stopped`, same as a stop observed right after an incarnation ended.
                            final <- Some(Ok(SupervisionOutcome(last, restarts, StopReason.Stopped, stormPauses)))
                        | None, Some error ->
                            // Incarnations ran but none produced a `ProcessResult` — they failed to start
                            // (or to capture) and the loop was backing off before retrying. There is no
                            // honest `SupervisionOutcome` to report, so surface the real reason the child
                            // never came up, exactly as the `Error` branch below does for a stop that
                            // lands one moment earlier. Which of the two paths sees the stop is a race;
                            // the reported failure must not depend on it.
                            final <- Some(Error error)
                        | None, None ->
                            // Not a single incarnation was ever started, so there is neither a result nor
                            // a failure to report — and fabricating an outcome would invent a child that
                            // never ran. `Cancelled` is the existing terminal "ended before it produced
                            // anything" shape the loop already reports for a cancelled run.
                            final <- Some(Error(ProcessError.Cancelled program))
                    else
                        attempt <- attempt + 1
                        let attemptNumber = attempt

                        match! captureIncarnation attemptNumber command with
                        | Ok result ->
                            lastResult <- Some result

                            emit (fun () -> SupervisionEvent.IncarnationFinished(program, attemptNumber, result))

                            // Read-and-reset once per incarnation whether its liveness monitor forced this
                            // exit, so the restart below (if any) is attributed to `RestartCause.Liveness`.
                            let livenessCausedExit = takeLivenessTripped ()

                            if isStopping () then
                                // The current incarnation was gracefully stopped (or completed while a
                                // stop was pending): end with its honest result and `Stopped`, wins over
                                // policy/predicate — the caller explicitly asked to stop.
                                final <- Some(Ok(SupervisionOutcome(result, restarts, StopReason.Stopped, stormPauses)))
                            else
                                match stopWhenMatches result with
                                | Error error -> final <- Some(Error error)
                                | Ok predicateMatched ->
                                    if predicateMatched then
                                        final <-
                                            Some(
                                                Ok(
                                                    SupervisionOutcome(
                                                        result,
                                                        restarts,
                                                        StopReason.Predicate,
                                                        stormPauses
                                                    )
                                                )
                                            )
                                    else
                                        let crashed = not result.IsSuccess

                                        let wantsRestart =
                                            match config.Policy with
                                            | RestartPolicy.Always -> true
                                            | RestartPolicy.OnCrash -> crashed
                                            | RestartPolicy.Never -> false

                                        if not wantsRestart then
                                            final <-
                                                Some(
                                                    Ok(
                                                        SupervisionOutcome(
                                                            result,
                                                            restarts,
                                                            StopReason.PolicySatisfied,
                                                            stormPauses
                                                        )
                                                    )
                                                )
                                        elif crashed then
                                            match giveUpMatches result.FailureError with
                                            | Error error -> final <- Some(Error error)
                                            | Ok true ->
                                                emit (fun () -> SupervisionEvent.GaveUp(program, attemptNumber))

                                                final <-
                                                    Some(
                                                        Ok(
                                                            SupervisionOutcome(
                                                                result,
                                                                restarts,
                                                                StopReason.GaveUp,
                                                                stormPauses
                                                            )
                                                        )
                                                    )
                                            | Ok false when budgetExhausted () ->
                                                final <-
                                                    Some(
                                                        Ok(
                                                            SupervisionOutcome(
                                                                result,
                                                                restarts,
                                                                StopReason.RestartsExhausted,
                                                                stormPauses
                                                            )
                                                        )
                                                    )
                                            | Ok false ->
                                                match! stormGate (resultContext result) with
                                                | Error error -> final <- Some(Error error)
                                                | Ok() ->
                                                    if
                                                        result.Duration >= config.MaxBackoff && not result.IsTimedOut
                                                    then
                                                        escalation <- 0

                                                    let cause =
                                                        if livenessCausedExit then
                                                            RestartCause.Liveness
                                                        else
                                                            RestartCause.Exit

                                                    match!
                                                        sleepBackoff
                                                            escalation
                                                            (restarts + 1)
                                                            cause
                                                            (resultContext result)
                                                    with
                                                    | Error error -> final <- Some(Error error)
                                                    | Ok() ->
                                                        escalation <- escalation + 1
                                                        bumpRestarts ()
                                        elif budgetExhausted () then
                                            final <-
                                                Some(
                                                    Ok(
                                                        SupervisionOutcome(
                                                            result,
                                                            restarts,
                                                            StopReason.RestartsExhausted,
                                                            stormPauses
                                                        )
                                                    )
                                                )
                                        else
                                            match!
                                                sleepBackoff
                                                    escalation
                                                    (restarts + 1)
                                                    RestartCause.Exit
                                                    (resultContext result)
                                            with
                                            | Error error -> final <- Some(Error error)
                                            | Ok() ->
                                                escalation <- escalation + 1
                                                bumpRestarts ()
                        | Error error ->
                            emit (fun () -> SupervisionEvent.IncarnationFailed(program, attemptNumber, error))

                            // Consume the per-incarnation verdict even when capture itself failed: a
                            // live child can fault its pump after the monitor has stopped it.
                            let livenessCausedError = takeLivenessTripped ()

                            match error with
                            | ProcessError.Cancelled _ -> final <- Some(Error error)
                            | _ ->
                                let wantsRestart =
                                    match config.Policy with
                                    | RestartPolicy.Never -> false
                                    | _ -> ProcessError.isTransient error

                                if isStopping () then
                                    // A graceful stop was requested; end now rather than restart. A run
                                    // that never produced a result has none to report, so surface the
                                    // honest terminal error (same shape as an exhausted budget here).
                                    final <- Some(Error error)
                                elif not wantsRestart then
                                    final <- Some(Error error)
                                else
                                    match giveUpMatches error with
                                    | Error callbackError -> final <- Some(Error callbackError)
                                    | Ok true ->
                                        emit (fun () -> SupervisionEvent.GaveUp(program, attemptNumber))
                                        final <- Some(Error error)
                                    | Ok false when budgetExhausted () -> final <- Some(Error error)
                                    | Ok false ->
                                        // Remember the reason this incarnation produced no result before
                                        // sleeping on it: a graceful stop can cut the backoff / storm pause
                                        // short, and the pre-spawn branch above then reports this error rather
                                        // than a synthetic one.
                                        lastError <- Some error

                                        match! stormGate (errorContext error) with
                                        | Error callbackError -> final <- Some(Error callbackError)
                                        | Ok() ->
                                            let cause =
                                                if livenessCausedError then
                                                    RestartCause.Liveness
                                                else
                                                    RestartCause.Exit

                                            match!
                                                sleepBackoff escalation (restarts + 1) cause (errorContext error)
                                            with
                                            | Error callbackError -> final <- Some(Error callbackError)
                                            | Ok() ->
                                                escalation <- escalation + 1
                                                bumpRestarts ()

                let terminal =
                    match final with
                    | Some result -> result
                    | None -> Error(ProcessError.Io "Supervisor loop ended without a final result.")

                // The last event of a session, published before the `finally` below closes the stream:
                // exactly what `Completion` is about to report, so a consumer draining the stream learns
                // how supervision ended without also having to await the session.
                match terminal with
                | Ok outcome -> emit (fun () -> SupervisionEvent.Stopped(program, outcome.Stopped))
                | Error error -> emit (fun () -> SupervisionEvent.SupervisionFailed(program, error))

                return terminal
            finally
                // Always flip the live status to inactive before the loop's task completes, so an observer
                // that awaits `Completion` then reads `Status` never sees `IsActive = true` on a finished
                // (or faulted) session.
                markInactive ()

                // The loop is the only consumer of this source's token. Once it has ended, release the
                // cancellation registrations it owns; a late concurrent `StopAsync` is handled by
                // `requestGracefulStop`'s ObjectDisposedException guard. Both disposals run under `gate`,
                // serialized against `requestGracefulStop`'s `Cancel()` (see there) so the two can never
                // interleave, and `sleepCts` — the linked source, registered to propagate `stopCts`'s
                // cancellation — is disposed first so it can never be caught mid-teardown by a racing
                // `Cancel()`'s callback invocation. `sleepCts`'s own `use` still disposes it again once
                // this function returns, but `Dispose()` is idempotent, so that is a harmless no-op.
                lock gate (fun () ->
                    // Close the opt-in event stream in the SAME critical section as the cancellation
                    // teardown, so no emission (the loop's own, or a still-unwinding liveness monitor's)
                    // can land between "supervision ended" and "the stream is closed", and a consumer's
                    // enumeration ends once it has drained what is queued. Completing a channel built with
                    // `AllowSynchronousContinuations = false` never runs consumer code inline, so this
                    // cannot execute anything else while `gate` is held.
                    eventBuffer |> Option.iter (fun buffer -> buffer.Complete())

                    sleepCts.Dispose()
                    stopCts.Dispose())
        }

    // Launch the loop in the background as the constructor's last step. `runLoop` yields before any real
    // work, so this returns an already-suspended, not-yet-completed task immediately.
    let completion = runLoop ()

    /// A consistent live snapshot of this session's state (activity, restart count, storm-pause flag,
    /// and the current live incarnation's pid/start time). Cheap and lock-guarded — safe to poll from
    /// any thread, e.g. a health check, without racing the supervision loop.
    member _.Status: SupervisionStatus = lock gate (fun () -> status)

    /// The task that resolves to the final `SupervisionOutcome` (or a terminal `ProcessError`) when
    /// supervision ends — exactly what `Supervisor.RunAsync` returns. `await` it to block until
    /// supervision concludes on its own, via `StopAsync`, or via the `StartAsync` token's cancellation.
    member _.Completion: Task<Result<SupervisionOutcome, ProcessError>> = completion

    /// This session's typed lifecycle-event stream — incarnation starts and outcomes, launch-failure
    /// classes, scheduled restarts, storm pauses, health-check verdicts, give-up decisions, and the
    /// terminal reason — as an `IAsyncEnumerable<SupervisionEvent>` you drain while supervision runs
    /// (concurrently with `Completion`/`StopAsync`; the stream ends when supervision does).
    ///
    /// Requires the stream to have been enabled on the builder (`Supervisor.Events`), which is what
    /// gives the session somewhere to retain events from its very first incarnation — before any
    /// consumer could have asked for them. Without it this throws `InvalidOperationException`, rather
    /// than handing back a stream that would silently be missing the beginning of supervision.
    ///
    /// **One consumer.** Reading the buffer is destructive, so a second consumer would steal events
    /// from the first: call this once and share the enumeration, or a repeat call throws
    /// `InvalidOperationException`. Purely additive to the `OnRestart`/`OnStormPause` callbacks and the
    /// `Status` snapshot — enabling it changes no supervision decision, timing, or outcome.
    ///
    /// **Bounded, with an explicit gap marker.** The buffer holds at most the configured capacity of
    /// unread events. A consumer that keeps up loses nothing; one that falls behind (or never reads)
    /// makes the supervisor drop the OLDEST unread events to make room for newer ones — supervision is
    /// never slowed down or blocked by its observer. Each such gap is reported: the next event the
    /// consumer sees is a `SupervisionEventKind.EventsDropped` carrying exactly how many were lost,
    /// immediately before the oldest event that survived, and `DroppedEventCount` keeps the lifetime
    /// total.
    member _.EventsAsync() : IAsyncEnumerable<SupervisionEvent> =
        match eventBuffer with
        | None ->
            raise (
                InvalidOperationException
                    "This supervision session publishes no events. Enable the stream on the builder first, with Supervisor.Events()."
            )
        | Some buffer ->
            let claimed =
                lock gate (fun () ->
                    if eventsTaken then
                        false
                    else
                        eventsTaken <- true
                        true)

            if not claimed then
                raise (
                    InvalidOperationException
                        "This supervision session's event stream was already taken; a second consumer would steal events from the first."
                )

            // The consumer's every step is taken under the same `gate` the loop publishes under, so a
            // gap marker can never be reported out of order with the events around it. Only the wait
            // for more events happens outside the lock, on the channel itself.
            SupervisionEventStream(buffer.Reader, (fun () -> lock gate (fun () -> buffer.TryTakeNext())), eventProgram)
            :> IAsyncEnumerable<SupervisionEvent>

    /// How many events this session has dropped so far because the event stream's consumer fell behind
    /// its bounded capacity (or never read it) — the lifetime total behind the in-band
    /// `SupervisionEventKind.EventsDropped` markers, and the supervision analogue of
    /// `RunningProcess.DroppedStreamLineCount`. Always `0` when no stream was enabled, and while a
    /// consumer keeps up. Safe to read at any time, including after supervision has ended.
    member _.DroppedEventCount: int64 =
        match eventBuffer with
        | None -> 0L
        | Some buffer -> lock gate (fun () -> buffer.DroppedCount)

    /// Request a graceful stop with `gracePeriod`: stop a live-handle incarnation through its own
    /// graceful path (`RunningProcess.StopAsync`, honouring the grace window), or immediately cancel a
    /// capture-only incarnation (which has no process handle to stop gracefully), and end the supervision
    /// loop with `StopReason.Stopped`. A stopped capture-only run whose exit status is unavailable reports
    /// `Outcome.Unobserved` in its final result. Interrupts an in-flight backoff / storm pause so a stop
    /// taken between incarnations also ends promptly, and never launches a further incarnation. A
    /// stop that lands before *any* incarnation has produced a result has no result to report and no
    /// child of its own to stop, so supervision ends with `RunAsync`'s `Error` rather than starting one
    /// more child just to manufacture a `SupervisionOutcome`: the last failure that kept the child from
    /// starting (while backing off after runs that only ever failed to start), or
    /// `Error(ProcessError.Cancelled)` when the stop landed before the very first incarnation, so there
    /// is no failure to report either. That second shape is the one case where a `ProcessError.Cancelled`
    /// does not come from a cancelled `CancellationToken` (see `ProcessError.Cancelled`). Idempotent and
    /// race-safe against the loop and repeat calls.
    /// Returns the session's `Completion`, so a caller can `await` the final outcome directly. A
    /// negative `gracePeriod` is rejected with `ArgumentOutOfRangeException`; `TimeSpan.Zero` escalates
    /// the child kill immediately.
    member _.StopAsync(gracePeriod: TimeSpan) : Task<Result<SupervisionOutcome, ProcessError>> =
        ArgumentOutOfRangeException.ThrowIfLessThan(gracePeriod, TimeSpan.Zero)

        // Record the request, snapshot the current child, and interrupt any in-flight backoff / storm
        // sleep (see `requestGracefulStop`).
        let child = requestGracefulStop gracePeriod

        task {
            match child with
            | Some running ->
                // Stop the live child through its graceful path; the loop ends with `Stopped` once the
                // in-flight capture returns that child's honest result.
                let! _ = running.StopAsync gracePeriod
                return! completion
            | None ->
                // No live child to stop right now: a stop taken before the first spawn, between
                // incarnations, or against a capture-only runner. `requestGracefulStop` has already
                // cancelled any published capture-only lever immediately; otherwise `stopCts` cuts an
                // in-flight backoff / storm sleep short. The loop's pre-spawn `isStopping ()` check then
                // prevents another incarnation.
                return! completion
        }

    /// `StopAsync` using the default 2-second grace window (matching `RunningProcess.StopAsync`).
    member this.StopAsync() : Task<Result<SupervisionOutcome, ProcessError>> = this.StopAsync Limits.DefaultStopGrace

    /// Internal seam for hosting-style wrappers (`ProcessKit.Extensions.Hosting`): request the same
    /// graceful stop as `StopAsync` — set the `stopping` flag and interrupt the backoff / storm sleep so
    /// the loop ends promptly, launching no further incarnation — but additionally report the honest
    /// `Outcome` of the *live* child this call actually stopped (`Some outcome`), or `None` when there was
    /// no live child to stop (a between-incarnations / storm-pause stop, or a capture-only runner, whose
    /// cancellation lever is still triggered immediately). This
    /// lets a wrapper honour a "publish a last-stop outcome only for a real child stop" contract without
    /// racing the loop for the current child: the snapshot is taken atomically under `gate`, exactly as
    /// the loop publishes each incarnation. Unlike `StopAsync`, this does **not** await `Completion` — the
    /// caller awaits that separately (e.g. bounded by its own host-shutdown token). The live child's stop
    /// wait honours `cancellationToken`, and if that token fires before the child's own stop completes,
    /// the abandoned stop task's eventual fault is observed (via `observeFault`) so a late fault never
    /// surfaces unobserved at finalization.
    member internal _.StopActiveAsync
        (gracePeriod: TimeSpan, cancellationToken: CancellationToken)
        : Task<Outcome option> =
        ArgumentOutOfRangeException.ThrowIfLessThan(gracePeriod, TimeSpan.Zero)

        match requestGracefulStop gracePeriod with
        | None -> Task.FromResult None
        | Some running ->
            task {
                let stopTask = running.StopAsync gracePeriod

                try
                    let! outcome = stopTask.WaitAsync cancellationToken
                    return Some outcome
                with :? OperationCanceledException ->
                    // The host `cancellationToken` fired before the child's own stop completed; the stop
                    // keeps running detached (there is no way to force it to stop). Observe its eventual
                    // fault so a late fault — e.g. a pump fault while finishing off the child — never
                    // surfaces as an unobserved task exception at finalization.
                    observeFault stopTask
                    return None
            }

/// Keeps a `Command` alive: runs it, classifies every exit against the `RestartPolicy` and the
/// `StopWhen` predicate, and restarts it after an exponential-backoff delay until supervision ends.
///
/// `Command.Retry` answers "run this once, replaying on failure"; a supervisor answers the
/// different question **"keep this alive"** — a minimal `runit`/`systemd`-style keeper on top of
/// the runner layer. The two are distinct layers: a supervised command's own `Retry` is **not**
/// applied per incarnation (supervision runs the bare runner), so use the supervisor's own restart
/// policy and backoff instead. Runs go through an `IProcessRunner` (the default `JobRunner`);
/// override with `WithRunner` to share a `ProcessGroup` or inject a test double.
///
/// Defaults: `OnCrash`, unlimited restarts, backoff `200ms × 2.0` capped at 30 s, jitter on,
/// failure-storm guard off (enable with `StormPause`; failure half-life 30 s, threshold 5.0).
///
/// **Observability while supervision runs.** `RunAsync` only reports its `SupervisionOutcome` at the
/// very end, which is unusable for a long-lived (potentially never-ending) supervised service — so
/// two callback seams, `OnRestart` and `OnStormPause`, report restarts and storm pauses *live*, as
/// they happen (e.g. for a health check or crash-loop alerting). Both callbacks are invoked
/// synchronously, from the supervision loop itself (the same async context driving `RunAsync`),
/// right before the corresponding delay is slept out — so a slow or blocking handler delays every
/// restart/pause; keep handlers quick and non-blocking. Neither callback changes
/// `SupervisionOutcome`'s semantics — `Restarts`/`StormPauses`/`Stopped` are unaffected and remain
/// the authoritative final tally; the callbacks are an additive, best-effort live view.
///
/// **Interactive supervision.** For a poll-and-control view — a live `Status` snapshot (activity,
/// restart count, storm-pause flag, the current child's pid/start time), a graceful `StopAsync`, and a
/// `Completion` task — use `StartAsync`, which returns a live `SupervisionSession` handle. `RunAsync`
/// is a thin wrapper over `StartAsync` + awaiting `Completion`; the `Status` snapshot *adds* to the
/// `OnRestart`/`OnStormPause` callbacks without replacing them.
///
/// **Event stream.** Where the callbacks push two specific transitions into your code, `Events` opts in
/// to the whole lifecycle as a *pull*-based `IAsyncEnumerable<SupervisionEvent>`
/// (`SupervisionSession.EventsAsync`): starts, outcomes, launch-failure classes, restarts, storm pauses,
/// health-check verdicts, give-ups, and the terminal reason, each a non-secret typed value. It is a
/// third additive view alongside the callbacks and `Status`, never a replacement — and because a
/// supervisor must not be pacing itself against its observer, its buffer is bounded and drops the oldest
/// unread events (counted, and marked in-band) instead of applying backpressure.
[<Sealed>]
type Supervisor internal (config: SupervisorConfig) =

    /// Supervise `command` with the default `JobRunner` (a fresh private kill-on-drop group per
    /// incarnation).
    new(command: Command) =
        ArgumentNullException.ThrowIfNull(command, nameof command)
        Supervisor(SupervisorConfig.create command)

    /// Run every incarnation through `runner` instead of the default `JobRunner` — e.g. a shared
    /// `ProcessGroup` runner for one kill-on-drop group, or a test double.
    member _.WithRunner(runner: IProcessRunner) =
        ArgumentNullException.ThrowIfNull(runner, nameof runner)
        Supervisor({ config with Runner = runner })

    /// Bound (or widen) the output captured from each incarnation. The default is a bounded tail;
    /// pass `OutputBufferPolicy.Unbounded` to retain everything.
    member _.Capture(policy: OutputBufferPolicy) =
        ArgumentNullException.ThrowIfNull(policy, nameof policy)
        Supervisor({ config with Capture = policy })

    /// When to restart (default: `OnCrash`).
    member _.Restart(policy: RestartPolicy) =
        Supervisor({ config with Policy = policy })

    /// Restart at most `count` times — `count + 1` total runs (default: unlimited). `count` must be
    /// non-negative (`0` means no restarts at all — a single run; a negative value is rejected with
    /// `ArgumentOutOfRangeException`).
    member _.MaxRestarts(count: int) =
        ArgumentOutOfRangeException.ThrowIfNegative count
        Supervisor({ config with MaxRestarts = Some count })

    /// Exponential backoff before each restart: the delay is `base × factor^n`, capped by `MaxBackoff`,
    /// where `n` is an escalation exponent that climbs by one per restart but **resets to 0 after a
    /// healthy incarnation** (one that stayed up at least as long as `MaxBackoff` and wasn't a hang
    /// killed by its timeout) — so a long-lived service that crashes occasionally restarts promptly
    /// instead of being pinned at the ceiling. `n` is not the lifetime restart count
    /// (`SupervisionOutcome.Restarts`). A `factor` below `1.0` (or non-finite) is treated as `1.0`.
    /// A negative `baseDelay` is rejected with `ArgumentOutOfRangeException`; `TimeSpan.Zero` is
    /// accepted and restarts with no backoff delay. Default: `200ms × 2.0`.
    member _.Backoff(baseDelay: TimeSpan, factor: float) =
        ArgumentOutOfRangeException.ThrowIfLessThan(baseDelay, TimeSpan.Zero)

        Supervisor(
            { config with
                BackoffBase = baseDelay
                BackoffFactor = factor }
        )

    /// Cap any single backoff delay (default: 30 s). A negative `cap` is rejected with
    /// `ArgumentOutOfRangeException` — besides being a nonsensical negative ceiling, a negative cap
    /// would make the healthy-incarnation escalation reset (`result.Duration >= MaxBackoff` in
    /// `RunAsync`) fire after *every* incarnation, so the backoff would never climb. `TimeSpan.Zero`
    /// is accepted (every backoff delay is then capped to zero — restart immediately).
    member _.MaxBackoff(cap: TimeSpan) =
        ArgumentOutOfRangeException.ThrowIfLessThan(cap, TimeSpan.Zero)
        Supervisor({ config with MaxBackoff = cap })

    /// Multiply each backoff delay by a uniform factor in `[0.5, 1.5)` (default: **on**), so a
    /// fleet of supervised workers restarted by the same incident does not stampede back in
    /// lockstep. Disable for deterministic delays.
    member _.Jitter(enabled: bool) =
        Supervisor({ config with Jitter = enabled })

    /// Enable the **failure-storm guard**: when crash-restarts cluster faster than the failure
    /// score can decay, pause restarts once for `pause` (jittered per `Jitter`), then reset the
    /// score and resume. Off by default. Pauses taken are reported in
    /// `SupervisionOutcome.StormPauses`. A negative `pause` is rejected with
    /// `ArgumentOutOfRangeException`; `TimeSpan.Zero` is accepted and still counts as a storm pause
    /// (it resets the score, increments `StormPauses`, and fires `OnStormPause`) but sleeps out no
    /// real time — enabling the guard's accounting without a wait.
    member _.StormPause(pause: TimeSpan) =
        ArgumentOutOfRangeException.ThrowIfLessThan(pause, TimeSpan.Zero)
        Supervisor({ config with StormPause = Some pause })

    /// Half-life of the failure score used by the storm guard (default: 30 s). A zero half-life
    /// keeps no history (every failure scores exactly `1.0`). A negative `decay` is rejected with
    /// `ArgumentOutOfRangeException`. No effect unless `StormPause` is set.
    member _.FailureDecay(decay: TimeSpan) =
        ArgumentOutOfRangeException.ThrowIfLessThan(decay, TimeSpan.Zero)
        Supervisor({ config with FailureDecay = decay })

    /// Failure score above which the storm guard trips (default: `5.0`). A non-finite threshold
    /// never trips. No effect unless `StormPause` is set.
    member _.FailureThreshold(threshold: float) =
        Supervisor(
            { config with
                FailureThreshold = threshold }
        )

    /// End supervision when `predicate` matches a completed run — checked before the
    /// `RestartPolicy` on every exit, clean or not. (It never sees a run that failed to *start*;
    /// spawn errors are classified by the policy alone.)
    ///
    /// If `predicate` throws, supervision ends with `Error(ProcessError.Io ...)`. The callback
    /// exception never escapes `RunAsync` or `SupervisionSession.Completion`; the error detail names
    /// `StopWhen` and retains the completed result context. The session still performs normal teardown.
    member _.StopWhen(predicate: Func<ProcessResult<string>, bool>) =
        ArgumentNullException.ThrowIfNull(predicate, nameof predicate)

        Supervisor(
            { config with
                StopWhen = Some(fun result -> predicate.Invoke result) }
        )

    /// Classify a crash — or a spawn/IO failure that never produced a result — as *permanent*, so
    /// the supervisor gives up instead of restarting it forever. `classifier` receives the
    /// `ProcessError` of the failed incarnation: for a crashed run (one that produced a
    /// `ProcessResult` but is not a success) that is the crash's own `ProcessResult.FailureError`
    /// projection; for a run that never produced a result at all, it is the runner's own error.
    /// This is a different seam than `StopWhen`, which classifies by *outcome*
    /// (`ProcessResult`) — `GiveUpWhen` classifies by *error kind*, independent of whether the
    /// incarnation ever ran.
    ///
    /// Not checked for a clean exit, nor for a run `StopWhen` already ended, nor for a crash the
    /// `RestartPolicy` itself would not have restarted (e.g. under `Never`) — those already stop
    /// supervision with a more specific reason. When checked, it runs *before* `MaxRestarts`: a
    /// permanent-failure verdict wins over "budget not yet exhausted". A crashed match reports
    /// `StopReason.GaveUp`; a match on a run that never produced a result has no result to report
    /// and surfaces the classified error directly as `RunAsync`'s `Error`, same as an exhausted
    /// budget on that path.
    ///
    /// Default: unset — a permanent failure restarts forever (throttled only by
    /// backoff/`MaxRestarts`/the storm guard), matching the prior behavior.
    ///
    /// If `classifier` throws, supervision ends with `Error(ProcessError.Io ...)`. The callback
    /// exception never escapes `RunAsync` or `SupervisionSession.Completion`; the error detail names
    /// `GiveUpWhen` and retains the classified error context. The session still performs normal teardown.
    member _.GiveUpWhen(classifier: Func<ProcessError, bool>) =
        ArgumentNullException.ThrowIfNull(classifier, nameof classifier)

        Supervisor(
            { config with
                GiveUpWhen = Some(fun error -> classifier.Invoke error) }
        )

    /// Observe restarts live: `handler` runs synchronously, from the supervision loop, right before
    /// each restart's backoff delay is slept out — after the failed/finished incarnation, before the
    /// next one starts. Invoked on every restart (a crash, a timeout, a retried transient runner
    /// error, or a liveness-probe failure), never for the initial run. The event's
    /// `SupervisorRestartEvent.Cause` distinguishes an ordinary `Exit` restart from a `Liveness` one
    /// (a live-but-unresponsive child the probe stopped). `handler` runs on the same async context
    /// driving `RunAsync`, so keep it quick and non-blocking — a slow handler delays every restart.
    /// If it throws, supervision ends with `Error(ProcessError.Io ...)`; the raw exception never
    /// escapes `RunAsync` or `SupervisionSession.Completion`, the error detail names `OnRestart` and
    /// retains the result/error context that led to the restart. Normal session teardown still runs.
    /// Otherwise this callback is purely additive: it does not change `SupervisionOutcome.Restarts` or
    /// any other final semantics.
    /// Default: unset.
    member _.OnRestart(handler: Action<SupervisorRestartEvent>) =
        ArgumentNullException.ThrowIfNull(handler, nameof handler)

        Supervisor(
            { config with
                OnRestart = Some(fun event -> handler.Invoke event) }
        )

    /// Observe failure-storm pauses live: `handler` runs synchronously, from the supervision loop,
    /// right before each pause is slept out — see `StormPause`. Same synchronous,
    /// keep-it-quick contract as `OnRestart`. No effect unless `StormPause` is set. Purely
    /// additive: does not change `SupervisionOutcome.StormPauses` or any other final semantics.
    /// If it throws, supervision ends with `Error(ProcessError.Io ...)`; the raw exception never
    /// escapes `RunAsync` or `SupervisionSession.Completion`, the error detail names `OnStormPause`
    /// and retains the result/error context that led to the pause. Normal session teardown still runs.
    /// Default: unset.
    member _.OnStormPause(handler: Action<SupervisorStormPauseEvent>) =
        ArgumentNullException.ThrowIfNull(handler, nameof handler)

        Supervisor(
            { config with
                OnStormPause = Some(fun event -> handler.Invoke event) }
        )

    /// Enable an **HTTP liveness probe**: every `interval`, poll `uri` with an HTTP GET and treat the
    /// *live* child as healthy when the response passes the default 2xx check. After
    /// `LivenessFailures` consecutive failed attempts (default 3) the supervisor **gracefully stops**
    /// the child (with the `LivenessGrace` window) and restarts it through the ordinary
    /// policy/backoff path — closing the "alive but no longer responding" gap that `RestartPolicy`
    /// (exit-driven) and `Command.IdleTimeout` (stdout-silence-driven) miss. Off by default.
    ///
    /// The probe checks an *external* endpoint the child serves; it never reads the child's stdout/
    /// stderr (those belong to the incarnation's own capture) and never appears in argv/env or a log.
    /// The first attempt runs one `interval` after the child starts, giving a natural startup window.
    /// Liveness needs a live child handle, so it applies only to a spawn-capable runner (the default),
    /// not a capture-only test double. A single attempt reuses the same poll/deadline core as
    /// `RunningProcess.WaitForHttpAsync`. A zero or negative `interval` is clamped to a safe 1 ms
    /// minimum so a configuration typo does not reject supervisor startup or create a hot loop. `uri`
    /// must be absolute; a relative URI throws `ArgumentException` while building the supervisor.
    member this.LivenessHttp(uri: Uri, interval: TimeSpan) =
        ReadinessProbe.validateAbsoluteUri uri

        this.LivenessHttp(uri, ReadinessProbe.defaultHttpSuccess, interval)

    /// Like `LivenessHttp(uri, interval)`, but sends requests through the caller-owned `client`.
    /// ProcessKit reuses the client across attempts and never mutates or disposes it.
    member this.LivenessHttp(uri: Uri, client: HttpClient, interval: TimeSpan) =
        this.LivenessHttp(uri, client, ReadinessProbe.defaultHttpSuccess, interval)

    /// Like `LivenessHttp(uri, interval)`, but uses `isSatisfactory` to decide whether a response means
    /// the child is healthy (e.g. accept only a specific health-endpoint status/body). A zero or
    /// negative `interval` is clamped to a safe 1 ms minimum.
    member _.LivenessHttp(uri: Uri, isSatisfactory: Func<HttpResponseMessage, bool>, interval: TimeSpan) =
        ReadinessProbe.validateAbsoluteUri uri
        ArgumentNullException.ThrowIfNull(isSatisfactory, nameof isSatisfactory)

        Supervisor(
            { config with
                Liveness = Some(LivenessProbe.Http(uri, isSatisfactory, None))
                LivenessInterval = Liveness.clampInterval interval }
        )

    /// Like the predicate overload, but sends requests through the caller-owned `client`. ProcessKit
    /// reuses the client across attempts and never mutates or disposes it.
    member _.LivenessHttp
        (uri: Uri, client: HttpClient, isSatisfactory: Func<HttpResponseMessage, bool>, interval: TimeSpan)
        =
        ReadinessProbe.validateAbsoluteUri uri
        ArgumentNullException.ThrowIfNull(client, nameof client)
        ArgumentNullException.ThrowIfNull(isSatisfactory, nameof isSatisfactory)

        Supervisor(
            { config with
                Liveness = Some(LivenessProbe.Http(uri, isSatisfactory, Some client))
                LivenessInterval = Liveness.clampInterval interval }
        )

    /// Enable a **predicate liveness probe**: every `interval`, evaluate `probe` and treat the *live*
    /// child as healthy when it returns `true`. After `LivenessFailures` consecutive failed attempts
    /// the supervisor gracefully stops and restarts the child, exactly like `LivenessHttp`. Off by
    /// default. `probe` is the caller's own health check (a custom RPC, a file/socket poke, a metric
    /// read); a returned `false` or a raised exception both count as a failed attempt, and the API
    /// cannot force a caller-owned `probe` to stop, so a hung probe is bounded by `LivenessTimeout` and
    /// abandoned (its late outcome safely observed) rather than pinning the monitor. A zero or negative
    /// `interval` is clamped to a safe 1 ms minimum.
    member _.LivenessCheck(probe: Func<Task<bool>>, interval: TimeSpan) =
        ArgumentNullException.ThrowIfNull(probe, nameof probe)

        Supervisor(
            { config with
                Liveness = Some(LivenessProbe.Custom(fun () -> probe.Invoke()))
                LivenessInterval = Liveness.clampInterval interval }
        )

    /// Enable a whole-process-tree **memory liveness probe**. The supervisor samples attributable
    /// peak resident memory since the incarnation started every configured liveness interval and
    /// treats a value above `maxBytes` as a failed attempt. The peak is monotonic for that
    /// incarnation: once it crosses the limit, later lower current usage does not produce a healthy
    /// memory attempt. `LivenessFailures` therefore controls how many observations precede the
    /// restart, but cannot forgive an already-crossed peak. `maxBytes` must be positive.
    ///
    /// Whole-tree accounting requires a private Job Object or cgroup. If the active backend cannot
    /// provide an attributable metric, supervision ends with a typed `ProcessError.Unsupported`
    /// instead of silently falling back to leader-only or shared-group memory.
    member _.LivenessMemory(maxBytes: int64) =
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBytes, 1L, nameof maxBytes)

        Supervisor(
            { config with
                Liveness = Some(LivenessProbe.Memory maxBytes) }
        )

    /// Like `LivenessMemory(maxBytes)`, but also sets the sampling interval. A zero or negative
    /// interval is clamped to the same safe 1 ms minimum as the other liveness probes.
    member _.LivenessMemory(maxBytes: int64, interval: TimeSpan) =
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBytes, 1L, nameof maxBytes)

        Supervisor(
            { config with
                Liveness = Some(LivenessProbe.Memory maxBytes)
                LivenessInterval = Liveness.clampInterval interval }
        )

    /// How many **consecutive** failed liveness attempts trip a restart (default `3`). For HTTP and
    /// predicate probes, a single healthy attempt resets the run, so a flaky endpoint that recovers
    /// does not restart the child. For `LivenessMemory`, a healthy attempt resets the run only while
    /// the incarnation's peak is still at or below the limit; after the monotonic peak crosses it,
    /// later lower current usage remains failed. `count` must be at least `1`. No effect unless a
    /// liveness probe (`LivenessHttp`/`LivenessCheck`/`LivenessMemory`) is set.
    member _.LivenessFailures(count: int) =
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1, nameof count)
        Supervisor({ config with LivenessFailures = count })

    /// The per-attempt timeout for a liveness probe (default 2 s): one attempt gives the endpoint/
    /// predicate up to this long to prove healthy before it counts as a failure. `TimeSpan.Zero` is a
    /// meaningful fail-fast timeout: the attempt is immediately `NotReady` without invoking the probe;
    /// a negative value is rejected. No effect unless a liveness probe is set.
    member _.LivenessTimeout(timeout: TimeSpan) =
        ArgumentOutOfRangeException.ThrowIfLessThan(timeout, TimeSpan.Zero, nameof timeout)

        Supervisor(
            { config with
                LivenessTimeout = timeout }
        )

    /// The grace window passed to `RunningProcess.StopAsync` when a liveness failure forces a restart
    /// (default 2 s): the unresponsive child is asked to stop softly and hard-killed only if it does not
    /// exit within this window. `TimeSpan.Zero` intentionally escalates the kill immediately; a negative
    /// value is rejected. No effect unless a liveness probe is set.
    member _.LivenessGrace(grace: TimeSpan) =
        ArgumentOutOfRangeException.ThrowIfLessThan(grace, TimeSpan.Zero, nameof grace)
        Supervisor({ config with LivenessGrace = grace })

    /// Enable the **live event stream** on the sessions this supervisor starts: `StartAsync` then hands
    /// out a typed `SupervisionEvent` sequence through `SupervisionSession.EventsAsync`, reporting every
    /// incarnation start/outcome, launch-failure class, scheduled restart, storm pause, health-check
    /// verdict, give-up decision, and the terminal reason. Off by default, and purely additive: the
    /// `OnRestart`/`OnStormPause` callbacks and the `Status` snapshot keep working exactly as before, and
    /// no supervision decision, delay, or outcome depends on whether a stream is enabled or read.
    ///
    /// It is enabled here, on the builder, rather than discovered when a consumer first asks: the
    /// session must already be retaining events when its first incarnation starts, which happens as soon
    /// as `StartAsync` returns. Without this opt-in a session allocates no buffer and builds no event at
    /// all — `RunAsync` and every existing consumer pay nothing.
    ///
    /// `capacity` is the number of *unread* events the session retains (must be at least 1). Because an
    /// observer must never be able to stall a supervisor, a consumer that falls behind does not apply
    /// backpressure: the oldest unread events are dropped to make room for newer ones, and the gap is
    /// reported explicitly — see `SupervisionSession.EventsAsync` and `DroppedEventCount`.
    member _.Events(capacity: int) =
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1, nameof capacity)

        Supervisor(
            { config with
                EventCapacity = Some capacity }
        )

    /// `Events` with the default capacity of 128 unread events — deep enough that an ordinary consumer
    /// never lags (one crash-restart cycle costs three events), shallow enough that a stream nobody
    /// reads still cannot grow the supervisor's memory.
    member this.Events() =
        this.Events Supervision.DefaultEventCapacity

    /// Internal test seam: inject a virtual clock (advance-on-sleep) for deterministic timing tests.
    member internal _.WithClock(now: unit -> float, sleep: TimeSpan -> CancellationToken -> Task) =
        Supervisor({ config with Now = now; Sleep = sleep })

    /// Internal test seam: replace the liveness monitor's delay without changing its background-task
    /// execution model, so interval and startup-delay tests can be deterministic.
    member internal _.WithLivenessDelay(delay: TimeSpan -> CancellationToken -> Task) =
        Supervisor({ config with LivenessDelay = delay })

    /// Start supervising and return a live `SupervisionSession` handle — the interactive counterpart to
    /// `RunAsync`. Supervision runs in the background from the moment this returns; poll the session's
    /// `Status` for a live snapshot (activity, restart count, storm-pause flag, current child pid/start
    /// time), ask it to stop gracefully with `StopAsync`, or `await` its `Completion` for the final
    /// `SupervisionOutcome` (which is exactly what `RunAsync` would have returned).
    ///
    /// Returns a already-resolved `Task<SupervisionSession>`: the session is created synchronously (the
    /// background loop yields before its first spawn), and the `Task` shape keeps the verb consistent
    /// with `Command.StartAsync` and leaves room to await first-spawn readiness in a future revision.
    member _.StartAsync([<Optional>] cancellationToken: CancellationToken) : Task<SupervisionSession> =
        Task.FromResult(SupervisionSession(config, cancellationToken))

    /// Supervise until the policy, the predicate, or the restart budget ends it, and report the
    /// `SupervisionOutcome`. A thin wrapper over `StartAsync` + awaiting the session's `Completion`, so
    /// its behaviour is identical to driving a `SupervisionSession` to its natural end.
    ///
    /// Returns `Error` when the *terminating* attempt failed to produce a result at all (a spawn/IO
    /// failure with no further restart allowed), or when one of the `StopWhen`, `GiveUpWhen`,
    /// `OnRestart`, or `OnStormPause` callbacks throws. A callback exception is converted to a
    /// terminal `Error(ProcessError.Io ...)`; it never escapes `RunAsync` or the session's
    /// `Completion` task, and normal teardown still runs. The error detail names the callback and
    /// retains the source context available at the failure: the completed `ProcessResult` for
    /// `StopWhen`, the classified `ProcessError` for `GiveUpWhen`, and the result/error context that
    /// led to the restart or storm pause for `OnRestart`/`OnStormPause`. A callback fault is terminal
    /// and is not retried. A spawn failure with restarts remaining counts as a crash and is retried.
    /// An incarnation cancelled via its token is terminal: supervision returns that `Cancelled`
    /// immediately, regardless of policy or budget.
    member this.RunAsync
        ([<Optional>] cancellationToken: CancellationToken)
        : Task<Result<SupervisionOutcome, ProcessError>> =
        task {
            let! session = this.StartAsync cancellationToken
            return! session.Completion
        }

/// Pipe-friendly entry points for `Supervisor`.
[<RequireQualifiedAccess>]
module Supervisor =

    /// Supervise `command` with the default `JobRunner`.
    let create (command: Command) = Supervisor(command)

// `RunAsync` is an instance method on `Supervisor` — call `supervisor.RunAsync()` directly.
