namespace ProcessKit

open System

/// Shared exponential-backoff and jitter math for command retries and supervision.
module internal Backoff =

    /// A safe ceiling for any computed delay, so jitter never overflows `Task.Delay`.
    let maxDelay = TimeSpan.FromMilliseconds(float Int32.MaxValue)

    /// `baseDelay × factor^exponent`, capped at `cap`.
    let exponentialDelay (baseDelay: TimeSpan) (factor: float) (exponent: int) (cap: TimeSpan) : TimeSpan =
        if baseDelay <= TimeSpan.Zero || cap <= TimeSpan.Zero then
            TimeSpan.Zero
        else
            let scaled = baseDelay.TotalSeconds * (factor ** float exponent)

            if not (Double.IsFinite scaled) || scaled >= cap.TotalSeconds then
                cap
            else
                min (TimeSpan.FromSeconds scaled) cap

    /// A pseudo-random factor in `[0.5, 1.5)` from a source returning a sample in `[0, 1)`.
    let jitterFactor (nextDouble: unit -> float) = 0.5 + nextDouble ()

    /// Multiply `delay` by a uniform random factor in `[0.5, 1.5)` when `enabled`, always clamped to
    /// `[0, maxDelay]` so the result is safe to hand to `Task.Delay`.
    let applyJitter (delay: TimeSpan) (enabled: bool) (nextDouble: unit -> float) : TimeSpan =
        if delay <= TimeSpan.Zero then
            TimeSpan.Zero
        else
            let scaled =
                if enabled then
                    delay.TotalSeconds * jitterFactor nextDouble
                else
                    delay.TotalSeconds

            if not (Double.IsFinite scaled) || scaled >= maxDelay.TotalSeconds then
                maxDelay
            else
                TimeSpan.FromSeconds scaled

/// How a command retry computes the delay before its next attempt.
[<RequireQualifiedAccess; NoComparison>]
type internal RetryDelayPolicy =
    | Fixed of delay: TimeSpan
    | Exponential of baseDelay: TimeSpan * factor: float * maxDelay: TimeSpan * jitter: bool

module internal RetryDelayPolicy =

    let delay (retryNumber: int) (nextDouble: unit -> float) (policy: RetryDelayPolicy) =
        match policy with
        | RetryDelayPolicy.Fixed fixedDelay -> fixedDelay
        | RetryDelayPolicy.Exponential(baseDelay, factor, maxDelay, jitter) ->
            let exponent = max 0 (retryNumber - 1)
            let backoff = Backoff.exponentialDelay baseDelay factor exponent maxDelay
            Backoff.applyJitter backoff jitter nextDouble
