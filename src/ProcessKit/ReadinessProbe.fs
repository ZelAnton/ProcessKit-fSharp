namespace ProcessKit

open System
open System.Diagnostics
open System.Net
open System.Net.Sockets
open System.Threading
open System.Threading.Tasks

/// Readiness probes for a started process that never touch its state: they poll an external
/// condition (a reachable TCP endpoint, an arbitrary async predicate) and report through a `Result`,
/// using only the program name for the `NotReady`/`Cancelled` error. Factored out of `RunningProcess`
/// because they share none of its pipe/streaming/exit machinery; the `WaitForPortAsync`/`WaitForAsync`
/// members are thin wrappers that null-check and delegate here.
module internal ReadinessProbe =

    /// Wait until a TCP connection to `endpoint` succeeds, or fail with `NotReady` after `timeout`
    /// (or `Cancelled` if `cancellationToken` fires first). Does not read the child's stdout/stderr
    /// while polling.
    let waitForPort
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

    /// Poll `probe` until it returns true, or fail with `NotReady` after `timeout` (or `Cancelled`
    /// if `cancellationToken` fires first). Like `waitForPort`, does not drain the child's
    /// stdout/stderr while polling.
    let waitFor
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
