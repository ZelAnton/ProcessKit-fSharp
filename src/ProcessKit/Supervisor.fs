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

    /// The `Supervisor.GiveUpWhen` classifier recognized a crash as *permanent* — the supervisor
    /// stopped instead of restarting it forever. Only reported for a crashed run that produced a
    /// `ProcessResult` (the classifier still receives that crash's `ProcessError` projection, via
    /// `ProcessResult.FailureError`); a permanent failure that never produced a result (a spawn/IO
    /// failure the classifier also recognizes) has no result to report and instead surfaces
    /// directly as `RunAsync`'s `Error`, same as an exhausted budget on that path.
    | GaveUp

/// A single restart, reported live from the supervision loop (see `Supervisor.OnRestart`) — not to
/// be confused with the final `SupervisionOutcome.Restarts` count.
///
/// Sealed with an internal constructor so it can gain fields without breaking the frozen API.
[<Sealed>]
type SupervisorRestartEvent internal (program: string, restart: int, delay: TimeSpan) =

    /// The supervised command's program name.
    member _.Program = program

    /// The 1-based lifetime restart number — matches `SupervisionOutcome.Restarts` once this
    /// restart becomes the last one.
    member _.Restart = restart

    /// The backoff delay the supervisor is about to sleep out before this restart.
    member _.Delay = delay

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
      GiveUpWhen: (ProcessError -> bool) option
      OnRestart: (SupervisorRestartEvent -> unit) option
      OnStormPause: (SupervisorStormPauseEvent -> unit) option
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
          GiveUpWhen = None
          OnRestart = None
          OnStormPause = None
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
    /// next one starts. Invoked on every restart (a crash, a timeout, or a retried transient runner
    /// error), never for the initial run. `handler` runs on the same async context driving
    /// `RunAsync`, so keep it quick and non-blocking — a slow handler delays every restart. Purely
    /// additive: does not change `SupervisionOutcome.Restarts` or any other final semantics.
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

    /// Internal test seam: inject a virtual clock (advance-on-sleep) for deterministic timing tests.
    member internal _.WithClock(now: unit -> float, sleep: TimeSpan -> CancellationToken -> Task) =
        Supervisor({ config with Now = now; Sleep = sleep })

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

        // Restart-capable: the policy could restart at least once more (`Never`, or an explicit
        // `MaxRestarts(0)`, never restarts at all — those are effectively a single run). Computed from
        // *configuration* alone, not from whether a restart actually happens at runtime: a one-shot
        // stdin source must be refused up front, before the first incarnation ever reads it, not only
        // once a crash would trigger the restart that exhausts it.
        let restartCapable =
            match config.Policy with
            | RestartPolicy.Never -> false
            | _ -> config.MaxRestarts |> Option.forall (fun limit -> limit > 0)

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

            // Seeded up front (instead of `None`) when the command is refused before its first
            // incarnation: a one-shot stdin source (`FromStream`/`FromLines`/`FromAsyncLines`) can only
            // be pumped once, so restarting the incarnation would silently feed the next one
            // empty/truncated input instead of replaying the original one. Fail loudly, before the first
            // incarnation ever runs, rather than let a restart quietly corrupt the input (T-088; ports
            // ProcessKit-rs `c1f39c7`/`8472007`). Seeding `final` here — rather than branching around the
            // loop — makes the `while final.IsNone` loop below skip its body outright, no restructuring
            // needed. `RestartPolicy.Never` / an explicit `MaxRestarts(0)` is unaffected (not
            // restart-capable), and so is every repeatable source (`Bytes`/`String`/`File`/`Empty`).
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
                            Diag.stormPaused program

                            match config.OnStormPause with
                            | Some handler -> handler (SupervisorStormPauseEvent(program, stormPauses + 1, jittered))
                            | None -> ()

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
                    Diag.supervisorRestarted program

                    match config.OnRestart with
                    | Some handler -> handler (SupervisorRestartEvent(program, restartNumber, delay))
                    | None -> ()

                    if delay > TimeSpan.Zero then
                        do! config.Sleep delay cancellationToken
                }
                :> Task

            let budgetExhausted () =
                config.MaxRestarts |> Option.exists (fun limit -> restarts >= limit)

            // True when `GiveUpWhen` is set and its classifier recognizes `error` as permanent.
            // Callers only consult this once a restart would otherwise be attempted (a policy that
            // already stops, or a `StopWhen`/terminal-error match, wins with its own more specific
            // reason and never reaches this check) — matching `StopWhen`'s own "checked before the
            // policy" placement but one step later, since a permanent verdict should win over an
            // exhausted budget, not just over the plain policy decision.
            let giveUpMatches (error: ProcessError) =
                config.GiveUpWhen |> Option.exists (fun classify -> classify error)

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
                            elif crashed && giveUpMatches result.FailureError then
                                final <- Some(Ok(SupervisionOutcome(result, restarts, StopReason.GaveUp, stormPauses)))
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

                            // A permanent-failure verdict on a run that never produced a result has no
                            // `ProcessResult` to report through `SupervisionOutcome`, so it surfaces the
                            // classified error directly — same as an exhausted budget or an already-terminal
                            // error on this path. Only consulted once a restart would otherwise be attempted
                            // (`wantsRestart`), so a classifier is never invoked for a policy/error class
                            // that already stops for its own, more specific reason.
                            if not wantsRestart || giveUpMatches error || budgetExhausted () then
                                final <- Some(Error error)
                            else
                                // A transient error produced no healthy incarnation, so the escalation only
                                // climbs here (no reset); it uses the shared exponent so a run that
                                // alternates transient errors and crashes backs off consistently.
                                do! stormGate ()
                                do! sleepBackoff escalation (restarts + 1)
                                escalation <- escalation + 1
                                restarts <- restarts + 1

            match final with
            | Some result -> return result
            | None -> return Error(ProcessError.Io "Supervisor loop ended without a final result.")
        }

/// Pipe-friendly entry points for `Supervisor`.
[<RequireQualifiedAccess>]
module Supervisor =

    /// Supervise `command` with the default `JobRunner`.
    let create (command: Command) = Supervisor(command)

// `RunAsync` is an instance method on `Supervisor` — call `supervisor.RunAsync()` directly.
