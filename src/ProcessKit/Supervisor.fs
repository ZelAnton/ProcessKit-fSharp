namespace ProcessKit

open System
open System.Diagnostics
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
    let maxDelay = TimeSpan.FromMilliseconds(float Int32.MaxValue)

    /// `base × factor^n`, capped at `cap`.
    let backoffDelay (baseDelay: TimeSpan) (factor: float) (n: int) (cap: TimeSpan) : TimeSpan =
        if baseDelay <= TimeSpan.Zero then
            TimeSpan.Zero
        else
            let scaled = baseDelay.TotalSeconds * (factor ** float n)

            if not (Double.IsFinite scaled) || scaled >= cap.TotalSeconds then
                cap
            else
                min (TimeSpan.FromSeconds scaled) cap

    /// A pseudo-random factor in `[0.5, 1.5)`.
    let jitterFactor () = 0.5 + Random.Shared.NextDouble()

    /// Multiply `delay` by a uniform random factor in `[0.5, 1.5)` when `enabled`, always clamped to
    /// `[0, maxDelay]` so the result is safe to hand to `Task.Delay` — even with jitter off and a
    /// large `MaxBackoff` / `StormPause`, the delay can never overflow the BCL timer.
    let applyJitter (delay: TimeSpan) (enabled: bool) : TimeSpan =
        if delay <= TimeSpan.Zero then
            TimeSpan.Zero
        else
            let scaled =
                if enabled then
                    delay.TotalSeconds * jitterFactor ()
                else
                    delay.TotalSeconds

            if not (Double.IsFinite scaled) || scaled >= maxDelay.TotalSeconds then
                maxDelay
            else
                TimeSpan.FromSeconds scaled

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
      Capture: OutputBufferPolicy
      // The clock seam: `Now` is a monotonic reading in seconds (only differences matter); `Sleep`
      // waits out a delay. Real implementations by default; tests inject a virtual clock that
      // advances `Now` when it `Sleep`s, so backoff/storm timing is deterministic.
      Now: unit -> float
      Sleep: TimeSpan -> CancellationToken -> Task }

module internal SupervisorConfig =

    let realNow () =
        float (Stopwatch.GetTimestamp()) / float Stopwatch.Frequency

    let realSleep (delay: TimeSpan) (cancellationToken: CancellationToken) : Task =
        task {
            try
                do! Task.Delay(delay, cancellationToken)
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
          Capture = Supervision.defaultCapture command
          Now = realNow
          Sleep = realSleep }

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

    /// Restart at most `count` times — `count + 1` total runs (default: unlimited).
    member _.MaxRestarts(count: int) =
        Supervisor({ config with MaxRestarts = Some count })

    /// Exponential backoff before each restart: the n-th restart (0-based) waits
    /// `base × factor^n`, capped by `MaxBackoff`. A `factor` below `1.0` (or non-finite) is
    /// treated as `1.0`. Default: `200ms × 2.0`.
    member _.Backoff(baseDelay: TimeSpan, factor: float) =
        Supervisor(
            { config with
                BackoffBase = baseDelay
                BackoffFactor = factor }
        )

    /// Cap any single backoff delay (default: 30 s).
    member _.MaxBackoff(cap: TimeSpan) =
        Supervisor({ config with MaxBackoff = cap })

    /// Multiply each backoff delay by a uniform factor in `[0.5, 1.5)` (default: **on**), so a
    /// fleet of supervised workers restarted by the same incident does not stampede back in
    /// lockstep. Disable for deterministic delays.
    member _.Jitter(enabled: bool) =
        Supervisor({ config with Jitter = enabled })

    /// Enable the **failure-storm guard**: when crash-restarts cluster faster than the failure
    /// score can decay, pause restarts once for `pause` (jittered per `Jitter`), then reset the
    /// score and resume. Off by default. Pauses taken are reported in
    /// `SupervisionOutcome.StormPauses`.
    member _.StormPause(pause: TimeSpan) =
        Supervisor({ config with StormPause = Some pause })

    /// Half-life of the failure score used by the storm guard (default: 30 s). A zero half-life
    /// keeps no history. No effect unless `StormPause` is set.
    member _.FailureDecay(decay: TimeSpan) =
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

    /// Internal test seam: inject a virtual clock (advance-on-sleep) for deterministic timing tests.
    member internal _.WithClock(now: unit -> float, sleep: TimeSpan -> CancellationToken -> Task) =
        Supervisor({ config with Now = now; Sleep = sleep })

    /// Supervise until the policy, the predicate, or the restart budget ends it, and report the
    /// `SupervisionOutcome`.
    ///
    /// Returns `Error` only when the *terminating* attempt failed to produce a result at all (a
    /// spawn/IO failure with no further restart allowed) — there is no final result to report. A
    /// spawn failure with restarts remaining counts as a crash and is retried. An incarnation
    /// cancelled via its token is terminal: supervision returns that `Cancelled` immediately,
    /// regardless of policy or budget.
    member _.RunAsync
        ([<Optional>] cancellationToken: CancellationToken)
        : Task<Result<SupervisionOutcome, ProcessError>> =
        let factor =
            if Double.IsFinite config.BackoffFactor then
                max config.BackoffFactor 1.0
            else
                1.0

        let command = config.Command.OutputBuffer config.Capture
        let program = config.Command.Program

        task {
            let mutable restarts = 0
            // The backoff *escalation* exponent — distinct from the lifetime `restarts` (which drives the
            // `MaxRestarts` budget and the reported count). It climbs per restart but RESETS after a
            // healthy incarnation, so a long-lived service that crashes occasionally isn't pinned at the
            // `MaxBackoff` ceiling forever.
            let mutable escalation = 0
            let mutable stormScore = 0.0
            let mutable lastFailureAt: float option = None
            let mutable stormPauses = 0
            let mutable final: Result<SupervisionOutcome, ProcessError> option = None

            // The failure-storm gate, run before the backoff of every *failure*-driven restart:
            // fold the failure into the decaying score and, past the threshold, sleep out one
            // jittered pause and reset the score (a fresh window).
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

                        // A non-finite threshold never trips (matches the doc): `Double.IsFinite` keeps
                        // `-infinity` from making the comparison true for every finite score.
                        if Double.IsFinite config.FailureThreshold && stormScore > config.FailureThreshold then
                            let jittered = Supervision.applyJitter pause config.Jitter
                            Log.stormPause config.Command.Config.Logger program jittered

                            if jittered > TimeSpan.Zero then
                                do! config.Sleep jittered cancellationToken

                            stormScore <- 0.0
                            lastFailureAt <- None
                            stormPauses <- stormPauses + 1
                }
                :> Task

            // `exponent` is the resettable backoff escalation; `restartNumber` is the 1-based *lifetime*
            // restart count for the log, so the logged number tracks `SupervisionOutcome.Restarts` (it
            // must NOT be the escalation, which resets after a healthy run).
            let sleepBackoff (exponent: int) (restartNumber: int) : Task =
                task {
                    let delay =
                        Supervision.backoffDelay config.BackoffBase factor exponent config.MaxBackoff

                    let delay = Supervision.applyJitter delay config.Jitter
                    Log.supervisorRestart config.Command.Config.Logger program restartNumber delay

                    if delay > TimeSpan.Zero then
                        do! config.Sleep delay cancellationToken
                }
                :> Task

            let budgetExhausted () =
                config.MaxRestarts |> Option.exists (fun limit -> restarts >= limit)

            while final.IsNone do
                if cancellationToken.IsCancellationRequested then
                    final <- Some(Error(ProcessError.Cancelled program))
                else
                    // Capture through the seam primitive directly: the supervisor owns the retry/restart
                    // policy, so it must not also pick up the command's verb-level `Retry` here.
                    match! config.Runner.CaptureStringAsync(command, cancellationToken) with
                    | Ok result ->
                        let predicateMatched =
                            match config.StopWhen with
                            | Some predicate -> predicate result
                            | None -> false

                        if predicateMatched then
                            final <- Some(Ok(SupervisionOutcome(result, restarts, StopReason.Predicate, stormPauses)))
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

                                // A healthy incarnation — one that stayed up at least as long as the backoff
                                // ceiling AND wasn't a hang killed by its own timeout — resets the escalation,
                                // so the next restart starts at the base delay again. A tight crash loop, or a
                                // hang that times out on every incarnation, does NOT clear the bar, so it keeps
                                // climbing and self-throttles.
                                if result.Duration >= config.MaxBackoff && not result.IsTimedOut then
                                    escalation <- 0

                                do! sleepBackoff escalation (restarts + 1)
                                escalation <- escalation + 1
                                restarts <- restarts + 1
                    | Error error ->
                        match error with
                        | ProcessError.Cancelled _ -> final <- Some(Error error)
                        | _ ->
                            // Restart only a *transient* error (a spawn race, transient I/O) — the only
                            // kind that can succeed on a re-run. Every other error is terminal: a
                            // deterministic startup/config failure (the program isn't found, the stdin
                            // source can't be read, a resource limit can't be enforced) would fail
                            // identically forever, and even a variable failure like an output-cap overflow
                            // is better surfaced than hidden behind an unbounded restart storm. Defers to
                            // `ProcessError.isTransient` so the classification lives in one place. An actual
                            // crash arrives on the `Ok` branch (a non-zero exit is data), so crash-restart
                            // is unaffected; `Cancelled` is already terminal above.
                            let wantsRestart =
                                match config.Policy with
                                | RestartPolicy.Never -> false
                                | _ -> ProcessError.isTransient error

                            if not wantsRestart || budgetExhausted () then
                                final <- Some(Error error)
                            else
                                // A transient error produced no healthy incarnation, so the escalation only
                                // climbs here (no reset); it uses the shared exponent so a run that
                                // alternates transient errors and crashes backs off consistently.
                                do! stormGate ()
                                do! sleepBackoff escalation (restarts + 1)
                                escalation <- escalation + 1
                                restarts <- restarts + 1

            return final.Value
        }

/// Pipe-friendly entry points for `Supervisor`.
[<RequireQualifiedAccess>]
module Supervisor =

    /// Supervise `command` with the default `JobRunner`.
    let create (command: Command) = Supervisor(command)

// `RunAsync` is an instance method on `Supervisor` — call `supervisor.RunAsync()` directly.
