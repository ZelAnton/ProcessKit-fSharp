namespace ProcessKit

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Logging

/// Bounds for arming an OS timer from a user `TimeSpan`. `Task.Delay`,
/// `CancellationTokenSource.CancelAfter`, and `System.Threading.Timer` all reject a delay outside
/// `[-1ms, Int32.MaxValue ms]` — feeding them an out-of-range span throws synchronously, which would
/// break the honest-result contract and orphan the in-flight output pumps. ProcessKit rejects a
/// negative timeout when it is configured and treats an over-long one as "no timeout".
module internal Timeouts =

    /// The largest delay the BCL timers accept (`Int32.MaxValue` ms ≈ 24.8 days).
    let maxArmable = TimeSpan.FromMilliseconds(float Int32.MaxValue)

    /// True when `duration` can be armed on a BCL timer (non-negative and within range). A larger
    /// positive span is "effectively never", so the run proceeds as if no timeout were set.
    let isArmable (duration: TimeSpan) =
        duration >= TimeSpan.Zero && duration <= maxArmable

    /// Clamp `duration` into the armable range so a BCL timer can be constructed without throwing:
    /// a negative span becomes zero (the timer fires immediately); an over-long one is capped at the
    /// max (~24.8 days). Used where a deadline must always be armed (e.g. a readiness probe).
    let clampArmable (duration: TimeSpan) =
        if duration < TimeSpan.Zero then TimeSpan.Zero
        elif duration > maxArmable then maxArmable
        else duration

    /// Race a process `wait` against a deadline. With no timeout (or one too large to arm) just returns
    /// `wait`. Otherwise: if the wait wins, cancel the timer and return its outcome; on the deadline,
    /// run `onTimeout` (the kill), await `wait` so the child is reaped, log, and report `TimedOut`.
    /// One home for the subtle CTS-cancel + reference-equality-winner logic shared by the run verbs and
    /// the group runner. (Negative is rejected by the builder; `isArmable` screens it out defensively,
    /// so `Task.Delay` here can never throw synchronously.)
    let raceTimeout
        (logger: ILogger option)
        (program: string)
        (runId: string)
        (timeout: TimeSpan option)
        (onTimeout: unit -> Task)
        (wait: Task<Outcome>)
        : Task<Outcome> =
        match timeout with
        | Some t when isArmable t ->
            task {
                use timeoutCts = new CancellationTokenSource()
                let waitBase = wait :> Task
                let! winner = Task.WhenAny(waitBase, Task.Delay(t, timeoutCts.Token))

                if obj.ReferenceEquals(winner, waitBase) then
                    timeoutCts.Cancel()
                    return! wait
                else
                    do! onTimeout ()
                    let! _ = wait
                    Log.timeout logger program t runId
                    return Outcome.TimedOut
            }
        | _ -> wait
