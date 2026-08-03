namespace ProcessKit

open System
open System.Diagnostics
open System.Net.Http
open System.Runtime.InteropServices
open System.Threading
open System.Threading.Tasks

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

    /// When the current live incarnation started, or `None` when no child is alive right now (see
    /// `Pid`).
    member _.StartTime = startTime

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

    // Loop-owned mirror fields (only the supervision loop writes them), republished into `status` under
    // `gate` on every change so external readers see a consistent snapshot.
    let mutable restarts = 0
    let mutable stormPaused = false
    let mutable active = true
    let mutable current: RunningProcess option = None

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
        let startTime = current |> Option.map (fun running -> running.StartTime)
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
            refresh ())

    let isStopping () = lock gate (fun () -> stopping)

    // Record a graceful-stop request and snapshot the current live child atomically under `gate` (closing
    // the `StopAsync`-vs-spawn race, see `publishCurrent`), then interrupt any in-flight backoff / storm
    // sleep so a stop taken *between* incarnations ends the loop promptly instead of waiting the delay out.
    // Shared by the public `StopAsync` and the internal `StopActiveAsync` hosting seam.
    let requestGracefulStop (gracePeriod: TimeSpan) : RunningProcess option =
        let child =
            lock gate (fun () ->
                stopGrace <- gracePeriod
                stopping <- true
                current)

        // `Cancel()` is serialized against `runLoop`'s teardown `Dispose()` under this same `gate` (see
        // its `finally`), so the two can never interleave: whichever call takes `gate` first either runs
        // `Cancel()` to completion while `stopCts` is still fully live (the linked `sleepCts` it notifies
        // is guaranteed not to be disposing concurrently), or finds `stopCts` already disposed and hits
        // the guard below cleanly. Without this lock, a `Cancel()` that starts just before a racing
        // `Dispose()` could have its linked-token callback throw `ObjectDisposedException` against the
        // already-torn-down `sleepCts`, which surfaces from `Cancel()` as an unhandled `AggregateException`
        // that this handler — written only for a direct `ObjectDisposedException` — would not catch.
        lock gate (fun () ->
            try
                stopCts.Cancel()
            with :? ObjectDisposedException ->
                // Already disposed by a concurrent `runLoop` teardown; the loop has already ended and
                // there is nothing further to cancel.
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
    let monitorLiveness
        (running: RunningProcess)
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
                                    observeFault (running.StopAsync grace)
                                else
                                    consecutiveFailures <- consecutiveFailures + 1

                                if not fatalResourceError && consecutiveFailures >= threshold then
                                    tripped <- true
                                    // Record the liveness verdict BEFORE stopping the child, so the loop
                                    // observes the flag once the graceful stop makes `OutputStringAsync`
                                    // return (the write is ordered before the stop under `gate`).
                                    markLivenessTripped ()
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
    let captureIncarnation (command: Command) : Task<Result<ProcessResult<string>, ProcessError>> =
        if not spawnCapable then
            config.Runner.CaptureStringAsync(command, cancellationToken)
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
                    | None -> return! config.Runner.CaptureStringAsync(command, cancellationToken)
                    | Some spawnTask ->
                        match! spawnTask with
                        | Error error -> return Error error
                        | Ok running ->
                            let pendingGrace = publishCurrent running
                            use _registration = effectiveToken.Register(fun () -> running.Kill())

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
                            let monitorTask = monitorLiveness running outputTask livenessCts.Token

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

        let restartCapable =
            match config.Policy with
            | RestartPolicy.Never -> false
            | _ -> config.MaxRestarts |> Option.forall (fun limit -> limit > 0)

        backgroundTask {
            // Force the async boundary before any real work, so the constructor returns before the first
            // incarnation is spawned (the whole configure-and-spawn prefix runs off the caller's thread).
            do! Task.Yield()

            // Sleeps observe both the caller's cancellation and a session stop, so a graceful stop (which
            // cancels `stopCts`) promptly interrupts an in-flight backoff / storm pause. The incarnation
            // capture, by contrast, keeps the *caller's* token only — a stop must gracefully stop the
            // child (`RunningProcess.StopAsync`), never hard-cancel it as an error.
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

            let stormGate () : Task =
                task {
                    match config.StormPause with
                    | None -> ()
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

                            match config.OnStormPause with
                            | Some handler -> handler (SupervisorStormPauseEvent(program, stormPauses + 1, jittered))
                            | None -> ()

                            // Bracket exactly the pause window in the live status: paused while the jittered
                            // sleep runs, cleared the instant it returns (or is cut short by a stop).
                            setStormPaused true

                            if jittered > TimeSpan.Zero then
                                do! config.Sleep jittered sleepToken

                            setStormPaused false
                            stormScore <- 0.0
                            lastFailureAt <- None
                            stormPauses <- stormPauses + 1
                }
                :> Task

            let sleepBackoff (exponent: int) (restartNumber: int) (cause: RestartCause) : Task =
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

                    match config.OnRestart with
                    | Some handler -> handler (SupervisorRestartEvent(program, restartNumber, delay, cause))
                    | None -> ()

                    if delay > TimeSpan.Zero then
                        do! config.Sleep delay sleepToken
                }
                :> Task

            let budgetExhausted () =
                config.MaxRestarts |> Option.exists (fun limit -> restarts >= limit)

            let giveUpMatches (error: ProcessError) =
                config.GiveUpWhen |> Option.exists (fun classify -> classify error)

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
                        match! captureIncarnation command with
                        | Ok result ->
                            lastResult <- Some result

                            // Read-and-reset once per incarnation whether its liveness monitor forced this
                            // exit, so the restart below (if any) is attributed to `RestartCause.Liveness`.
                            let livenessCausedExit = takeLivenessTripped ()

                            if isStopping () then
                                // The current incarnation was gracefully stopped (or completed while a
                                // stop was pending): end with its honest result and `Stopped`, wins over
                                // policy/predicate — the caller explicitly asked to stop.
                                final <- Some(Ok(SupervisionOutcome(result, restarts, StopReason.Stopped, stormPauses)))
                            else
                                let predicateMatched =
                                    match config.StopWhen with
                                    | Some predicate -> predicate result
                                    | None -> false

                                if predicateMatched then
                                    final <-
                                        Some(
                                            Ok(SupervisionOutcome(result, restarts, StopReason.Predicate, stormPauses))
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
                                    elif crashed && giveUpMatches result.FailureError then
                                        final <-
                                            Some(
                                                Ok(SupervisionOutcome(result, restarts, StopReason.GaveUp, stormPauses))
                                            )
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
                                        if crashed then
                                            do! stormGate ()

                                        if result.Duration >= config.MaxBackoff && not result.IsTimedOut then
                                            escalation <- 0

                                        let cause =
                                            if livenessCausedExit then
                                                RestartCause.Liveness
                                            else
                                                RestartCause.Exit

                                        do! sleepBackoff escalation (restarts + 1) cause
                                        escalation <- escalation + 1
                                        bumpRestarts ()
                        | Error error ->
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
                                elif not wantsRestart || giveUpMatches error || budgetExhausted () then
                                    final <- Some(Error error)
                                else
                                    // Remember the reason this incarnation produced no result before
                                    // sleeping on it: a graceful stop can cut the backoff / storm pause
                                    // short, and the pre-spawn branch above then reports this error rather
                                    // than a synthetic one.
                                    lastError <- Some error

                                    do! stormGate ()

                                    let cause =
                                        if livenessCausedError then
                                            RestartCause.Liveness
                                        else
                                            RestartCause.Exit

                                    do! sleepBackoff escalation (restarts + 1) cause
                                    escalation <- escalation + 1
                                    bumpRestarts ()

                match final with
                | Some result -> return result
                | None -> return Error(ProcessError.Io "Supervisor loop ended without a final result.")
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

    /// Request a graceful stop with `gracePeriod`: stop the current live incarnation through its own
    /// graceful path (`RunningProcess.StopAsync`, honouring the grace window) and end the supervision
    /// loop with `StopReason.Stopped` — reported as a normal `SupervisionOutcome` whose `FinalResult` is
    /// that incarnation's honest result, never a crash. Interrupts an in-flight backoff / storm pause so
    /// a stop taken between incarnations also ends promptly, and never launches a further incarnation. A
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
                // incarnations, or against a capture-only runner. The `stopCts` cancellation above cuts
                // any in-flight backoff / storm sleep short, and it is the loop's own pre-spawn
                // `isStopping ()` check that then ends supervision — without starting another
                // incarnation. (A capture-only incarnation already in flight has no graceful stop path,
                // so the loop ends through its ordinary post-capture stop branch once that capture
                // returns.)
                return! completion
        }

    /// `StopAsync` using the default 2-second grace window (matching `RunningProcess.StopAsync`).
    member this.StopAsync() : Task<Result<SupervisionOutcome, ProcessError>> = this.StopAsync Limits.DefaultStopGrace

    /// Internal seam for hosting-style wrappers (`ProcessKit.Extensions.Hosting`): request the same
    /// graceful stop as `StopAsync` — set the `stopping` flag and interrupt the backoff / storm sleep so
    /// the loop ends promptly, launching no further incarnation — but additionally report the honest
    /// `Outcome` of the *live* child this call actually stopped (`Some outcome`), or `None` when there was
    /// no live child to stop (a between-incarnations / storm-pause stop, or a capture-only runner). This
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
[<Sealed>]
type Supervisor internal (config: SupervisorConfig) =

    /// Supervise `command` with the default `JobRunner` (a fresh private kill-on-drop group per
    /// incarnation).
    new(command: Command) =
        ArgumentNullException.ThrowIfNull command
        Supervisor(SupervisorConfig.create command)

    /// Run every incarnation through `runner` instead of the default `JobRunner` — e.g. a shared
    /// `ProcessGroup` runner for one kill-on-drop group, or a test double.
    member _.WithRunner(runner: IProcessRunner) =
        ArgumentNullException.ThrowIfNull runner
        Supervisor({ config with Runner = runner })

    /// Bound (or widen) the output captured from each incarnation. The default is a bounded tail;
    /// pass `OutputBufferPolicy.Unbounded` to retain everything.
    member _.Capture(policy: OutputBufferPolicy) =
        ArgumentNullException.ThrowIfNull policy
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
    member _.StopWhen(predicate: Func<ProcessResult<string>, bool>) =
        ArgumentNullException.ThrowIfNull predicate

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
    member _.GiveUpWhen(classifier: Func<ProcessError, bool>) =
        ArgumentNullException.ThrowIfNull classifier

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
    /// Purely additive: does not change `SupervisionOutcome.Restarts` or any other final semantics.
    /// Default: unset.
    member _.OnRestart(handler: Action<SupervisorRestartEvent>) =
        ArgumentNullException.ThrowIfNull handler

        Supervisor(
            { config with
                OnRestart = Some(fun event -> handler.Invoke event) }
        )

    /// Observe failure-storm pauses live: `handler` runs synchronously, from the supervision loop,
    /// right before each pause is slept out — see `StormPause`. Same synchronous,
    /// keep-it-quick contract as `OnRestart`. No effect unless `StormPause` is set. Purely
    /// additive: does not change `SupervisionOutcome.StormPauses` or any other final semantics.
    /// Default: unset.
    member _.OnStormPause(handler: Action<SupervisorStormPauseEvent>) =
        ArgumentNullException.ThrowIfNull handler

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
        ArgumentNullException.ThrowIfNull isSatisfactory

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
        ArgumentNullException.ThrowIfNull client
        ArgumentNullException.ThrowIfNull isSatisfactory

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
        ArgumentNullException.ThrowIfNull probe

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

    /// Internal test seam: inject a virtual clock (advance-on-sleep) for deterministic timing tests.
    member internal _.WithClock(now: unit -> float, sleep: TimeSpan -> CancellationToken -> Task) =
        Supervisor({ config with Now = now; Sleep = sleep })

    /// Internal test seam: replace the liveness monitor's delay without changing its background-task
    /// execution model, so interval and startup-delay tests can be deterministic.
    member internal _.WithLivenessDelay(delay: TimeSpan -> CancellationToken -> Task) =
        Supervisor({ config with LivenessDelay = delay })

    /// Internal seam for hosting-style wrappers (e.g. `ProcessKit.Extensions.Hosting`) that need to
    /// combine an already-configured `StopWhen` with their own host-driven stop condition, without
    /// silently dropping whichever predicate the caller supplied via `StopWhen` before the wrapper
    /// ran. `Supervisor.StopWhen` itself *replaces* `config.StopWhen`, so a wrapper cannot safely
    /// call it again without first reading whatever predicate (if any) is already there.
    member internal _.CurrentStopWhen: (ProcessResult<string> -> bool) option =
        config.StopWhen

    /// Same seam as `CurrentStopWhen`, for `OnRestart`: lets a hosting-style wrapper chain its own
    /// live-restart tracking (e.g. an observable restart counter) onto whatever handler the caller
    /// already installed via `OnRestart`, instead of silently replacing it.
    member internal _.CurrentOnRestart: (SupervisorRestartEvent -> unit) option =
        config.OnRestart

    /// Same seam as `CurrentStopWhen`, for `OnStormPause`: lets a hosting-style wrapper chain its own
    /// live storm-pause tracking (e.g. an observable "currently paused" flag) onto whatever handler
    /// the caller already installed via `OnStormPause`, instead of silently replacing it.
    member internal _.CurrentOnStormPause: (SupervisorStormPauseEvent -> unit) option =
        config.OnStormPause

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
    /// Returns `Error` only when the *terminating* attempt failed to produce a result at all (a
    /// spawn/IO failure with no further restart allowed) — there is no final result to report. A
    /// spawn failure with restarts remaining counts as a crash and is retried. An incarnation
    /// cancelled via its token is terminal: supervision returns that `Cancelled` immediately,
    /// regardless of policy or budget.
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
