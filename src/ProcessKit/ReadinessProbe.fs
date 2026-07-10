namespace ProcessKit

open System
open System.Diagnostics
open System.IO
open System.Net
open System.Net.Sockets
open System.Threading
open System.Threading.Tasks

/// Readiness probes for a started process that never touch its state: they poll an external
/// condition (a reachable TCP endpoint, an arbitrary async predicate) and report through a `Result`,
/// using only the program name for the `NotReady`/`Cancelled` error. Factored out of `RunningProcess`
/// because they share none of its pipe/streaming/exit machinery; the `WaitForPortAsync`/`WaitForAsync`
/// members are thin wrappers that null-check and delegate here, additionally handing over the
/// child's `stdoutStream`/`stderrStream` (or `None`, when a buffered/streaming verb already owns
/// them) for the background drain below.
module internal ReadinessProbe =

    /// Background-drain `stdout`/`stderr` (discarding every byte) for the duration of `work`, so a
    /// child that writes more than one OS pipe buffer of startup output (~64 KiB on Linux) before
    /// becoming ready can't block in `write()` while a readiness probe polls — the same reason
    /// `WaitForLineAsync` reads stdout itself. Draining starts before `work`'s first attempt and is
    /// always stopped (cancelled, then awaited) once `work` concludes, whatever the outcome
    /// (success, `NotReady`, cancellation, or an unexpected fault), so it never outlives the probe
    /// and never races a later verb's own claim on the pipes. `stdout`/`stderr` are `None` when
    /// `RunningProcess` has already committed them to another consumer (a buffered capture or a
    /// streaming session already drains them itself), so this never starts a second, racing reader
    /// on the same pipe.
    ///
    /// A task computation expression cannot `do!` inside a `finally`, so this uses the established
    /// try/with-then-single-cleanup shape (see `RunningProcess.awaitBufferedOutcome`/`ProfileAsync`):
    /// capture any fault from `work`, always stop the drain, then re-raise the captured fault.
    /// `Pump.drainDiscardOrEmptyUntilDone` swallows cancellation and teardown races itself, so
    /// awaiting it here after cancelling never faults.
    ///
    /// Bytes drained here are discarded, not retained: like `WaitForLineAsync`, a capture started
    /// after the probe only sees what the child wrote AFTER the probe concluded — the documented
    /// "doesn't compose with a subsequent fresh capture" limitation now applies uniformly to all
    /// three readiness probes.
    let private withBackgroundDrain
        (stdout: Stream option)
        (stderr: Stream option)
        (work: unit -> Task<Result<'T, ProcessError>>)
        : Task<Result<'T, ProcessError>> =
        task {
            use drainCts = new CancellationTokenSource()
            let stdoutDrain = Pump.drainDiscardOrEmptyUntilDone stdout drainCts.Token
            let stderrDrain = Pump.drainDiscardOrEmptyUntilDone stderr drainCts.Token

            let mutable error: exn option = None
            let mutable result = Unchecked.defaultof<Result<'T, ProcessError>>

            try
                let! r = work ()
                result <- r
            with ex ->
                error <- Some ex

            drainCts.Cancel()
            do! Task.WhenAll(stdoutDrain, stderrDrain)

            match error with
            | Some ex -> return! Task.FromException<Result<'T, ProcessError>> ex
            | None -> return result
        }

    /// The `waitForPort` polling loop itself, unaware of draining — factored out so `waitForPort`
    /// can wrap it with `withBackgroundDrain` without duplicating the loop.
    let private waitForPortCore
        (program: string)
        (endpoint: IPEndPoint)
        (timeout: TimeSpan)
        (cancellationToken: CancellationToken)
        : Task<Result<unit, ProcessError>> =
        task {
            let deadline = Stopwatch.StartNew()
            let mutable connected = false

            while not connected
                  && not cancellationToken.IsCancellationRequested
                  && deadline.Elapsed < timeout do
                use attempt = CancellationTokenSource.CreateLinkedTokenSource cancellationToken
                attempt.CancelAfter(TimeSpan.FromSeconds 1.0)

                try
                    use client = new TcpClient()
                    do! client.ConnectAsync(endpoint.Address, endpoint.Port, attempt.Token)
                    connected <- true
                with _ ->
                    // Any connection failure (refused / timed out / unreachable) just means the
                    // server is not up yet — back off and retry until the deadline or cancellation.
                    try
                        do! Task.Delay(50, attempt.Token)
                    with :? OperationCanceledException ->
                        // The per-attempt 1s window or the caller's token cancelled the backoff; the
                        // loop guard re-checks the deadline/token next iteration and reports
                        // NotReady/Cancelled, so swallowing here is correct.
                        ()

            if connected then
                return Ok()
            elif cancellationToken.IsCancellationRequested then
                return Error(ProcessError.Cancelled program)
            else
                return Error(ProcessError.NotReady(program, timeout))
        }

    /// Wait until a TCP connection to `endpoint` succeeds, or fail with `NotReady` after `timeout`
    /// (or `Cancelled` if `cancellationToken` fires first). Background-drains (and discards) the
    /// child's piped `stdout`/`stderr` for the duration of the poll — see `withBackgroundDrain`.
    let waitForPort
        (program: string)
        (stdout: Stream option)
        (stderr: Stream option)
        (endpoint: IPEndPoint)
        (timeout: TimeSpan)
        (cancellationToken: CancellationToken)
        : Task<Result<unit, ProcessError>> =
        withBackgroundDrain stdout stderr (fun () -> waitForPortCore program endpoint timeout cancellationToken)

    /// The `waitFor` polling loop itself, unaware of draining — factored out so `waitFor` can wrap
    /// it with `withBackgroundDrain` without duplicating the loop.
    let private waitForCore
        (program: string)
        (probe: Func<Task<bool>>)
        (timeout: TimeSpan)
        (cancellationToken: CancellationToken)
        : Task<Result<unit, ProcessError>> =
        task {
            let deadline = Stopwatch.StartNew()
            let mutable ready = false

            while not ready
                  && not cancellationToken.IsCancellationRequested
                  && deadline.Elapsed < timeout do
                let! result = probe.Invoke()

                if result then
                    ready <- true
                else
                    try
                        do! Task.Delay(50, cancellationToken)
                    with :? OperationCanceledException ->
                        // Cancelled mid-backoff; the loop guard exits and reports Cancelled below.
                        ()

            if ready then
                return Ok()
            elif cancellationToken.IsCancellationRequested then
                return Error(ProcessError.Cancelled program)
            else
                return Error(ProcessError.NotReady(program, timeout))
        }

    /// Poll `probe` until it returns true, or fail with `NotReady` after `timeout` (or `Cancelled`
    /// if `cancellationToken` fires first). Background-drains (and discards) the child's piped
    /// `stdout`/`stderr` for the duration of the poll, like `waitForPort` — see `withBackgroundDrain`.
    let waitFor
        (program: string)
        (stdout: Stream option)
        (stderr: Stream option)
        (probe: Func<Task<bool>>)
        (timeout: TimeSpan)
        (cancellationToken: CancellationToken)
        : Task<Result<unit, ProcessError>> =
        withBackgroundDrain stdout stderr (fun () -> waitForCore program probe timeout cancellationToken)
