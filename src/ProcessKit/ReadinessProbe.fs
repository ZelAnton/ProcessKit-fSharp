namespace ProcessKit

open System
open System.Diagnostics
open System.IO
open System.Net
open System.Net.Http
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

    /// The fixed polling backoff between failed attempts in `waitForPortUsing`/`waitFor`.
    let private pollBackoff = TimeSpan.FromMilliseconds 50.0

    /// The backoff to use for the next poll: the smaller of the fixed `pollBackoff` and whatever time
    /// remains before `armedTimeout` elapses on `clock` — `TimeSpan.Zero` once the budget is already
    /// spent. Caps the fixed backoff so it can never itself carry a very short overall `timeout` past
    /// its own budget.
    let private remainingBackoff (clock: Stopwatch) (armedTimeout: TimeSpan) =
        let remaining = armedTimeout - clock.Elapsed

        if remaining <= TimeSpan.Zero then
            TimeSpan.Zero
        else
            min remaining pollBackoff

    /// Wait until `connect` completes for `endpoint`, or fail with `NotReady` once the shared `timeout`
    /// deadline elapses (or `Cancelled` if `cancellationToken` fires first). A non-positive `timeout` is
    /// an immediate `NotReady` — `connect` is never invoked. The `timeout` is clamped through
    /// `Timeouts.clampArmable` (an over-long span is capped at ~24.8 days), and that clamped value is
    /// what a resulting `NotReady` reports, so the reported budget never claims more than was enforced.
    ///
    /// Every connect attempt and polling backoff shares the same deadline token, so a short overall
    /// `timeout` can never be overrun by a longer fixed per-attempt window: each attempt is *raced*
    /// against a single shared `deadlineSignal` (not merely started with its own timer), so this returns
    /// the instant the remaining budget (or the caller's token) runs out — it does not wait for a
    /// non-cooperative connect (one that, like a real in-flight `TcpClient.ConnectAsync`, can ignore its
    /// own cancellation token once the OS has committed to the handshake) to finish on its own.
    ///
    /// The loop refuses to start a new attempt once the deadline has been *observed* to elapse — it
    /// checks both `linked.Token.IsCancellationRequested` and `clock.Elapsed >= armedTimeout` at the top
    /// of every iteration, not relying on the `CancellationTokenSource` timer callback having run yet.
    /// This is an honest, achievable contract, not the (unimplementable) absolute "no attempt ever
    /// starts after instant T": between evaluating that guard and the attempt actually starting there is
    /// an inherent scheduler-bounded window — a preemptive runtime (GC pause, OS scheduling) can suspend
    /// the thread after the check passes, so at most one attempt may begin marginally after the
    /// wall-clock deadline. No user-space code can close that window (it would require an atomic
    /// check-and-start against the wall clock, which the runtime does not offer), and it is harmless
    /// here: such a late attempt is immediately raced against the already-fired `deadlineSignal`, so it
    /// returns `NotReady` at once. Its late success is reported as `NotReady` (never a stale `Ok`), and
    /// its late fault is safely observed — an abandoned attempt keeps running in the background, but an
    /// `OnlyOnFaulted` continuation observes its eventual fault so it never surfaces as an unobserved
    /// task exception. This mirrors the best-effort contract the .NET BCL itself gives for
    /// `CancellationTokenSource.CancelAfter`: cancellation is *signaled* at the due time and *observed*
    /// at the next opportunity.
    ///
    /// `connect` is factored out (rather than hard-coding `TcpClient`) so tests can substitute a
    /// deterministically slow stand-in — exercising deadline cancellation of an in-flight attempt without
    /// depending on real network behaviour (e.g. an unassigned TEST-NET-1 address), which varies across
    /// sandboxed CI environments. `waitForPort` below is the production entry point, wired to a real
    /// `TcpClient` and to the background drain.
    let internal waitForPortUsing
        (connect: IPEndPoint -> CancellationToken -> Task)
        (program: string)
        (endpoint: IPEndPoint)
        (timeout: TimeSpan)
        (cancellationToken: CancellationToken)
        : Task<Result<unit, ProcessError>> =
        task {
            if cancellationToken.IsCancellationRequested then
                return Error(ProcessError.Cancelled program)
            elif timeout <= TimeSpan.Zero then
                return Error(ProcessError.NotReady(program, timeout))
            else
                // Clamp so an out-of-range timeout can't throw out of the CTS constructor. The clamped
                // value is also what gets reported in NotReady below — an over-long requested timeout is
                // silently capped at ~24.8 days, so reporting the raw, un-clamped value would claim a
                // budget longer than what was actually enforced.
                let armedTimeout = Timeouts.clampArmable timeout
                use timeoutCts = new CancellationTokenSource(armedTimeout)

                use linked =
                    CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, cancellationToken)

                let clock = Stopwatch.StartNew()

                // A standalone deadline signal — a task that only ever completes (cancelled) once
                // `linked` fires — reused across attempts: each connect attempt is raced against it so a
                // non-cooperative connect (one that ignores its own cancellation token) cannot block this
                // loop past the shared budget.
                let deadlineSignal = Task.Delay(Timeout.Infinite, linked.Token)

                let mutable connected = false
                let mutable stopped = false

                while not connected && not stopped do
                    if linked.Token.IsCancellationRequested || clock.Elapsed >= armedTimeout then
                        // The shared deadline (or the caller's token) already fired since the last
                        // backoff completed — do not start another connect attempt past the budget.
                        // Checked by elapsed time as well as by token, because a `CancellationTokenSource`
                        // timer callback is not guaranteed to have run yet even though its due time has
                        // already passed — relying on the token alone could start one more attempt after
                        // the deadline has, in wall-clock terms, already elapsed.
                        stopped <- true
                    else
                        let connectTask = connect endpoint linked.Token
                        let! winner = Task.WhenAny(connectTask, deadlineSignal)

                        if obj.ReferenceEquals(winner, deadlineSignal) then
                            // The shared deadline (or the caller's token) fired before `connect`
                            // completed. There is no way to force a non-cooperative connect attempt to
                            // stop, so let it keep running in the background — but observe its eventual
                            // outcome so a late fault is not left as an unobserved task exception.
                            stopped <- true

                            connectTask.ContinueWith(
                                (fun (finished: Task) -> finished.Exception |> ignore),
                                TaskContinuationOptions.OnlyOnFaulted
                                ||| TaskContinuationOptions.ExecuteSynchronously
                            )
                            |> ignore
                        else
                            try
                                do! connectTask
                                connected <- true
                            with
                            | :? OperationCanceledException ->
                                // The shared deadline elapsed, or the caller's token fired — either way
                                // there is no budget left for another attempt.
                                stopped <- true
                            | _ ->
                                // Any other connection failure (refused / unreachable) just means the
                                // server is not up yet — back off and retry, still bounded by the same
                                // shared token so the backoff itself can't run past the deadline. The
                                // backoff itself is also capped to whatever budget remains, so a fixed
                                // 50ms poll can't overrun a very short overall timeout on its own.
                                let backoff = remainingBackoff clock armedTimeout

                                if backoff <= TimeSpan.Zero then
                                    stopped <- true
                                else
                                    try
                                        do! Task.Delay(backoff, linked.Token)
                                    with :? OperationCanceledException ->
                                        stopped <- true

                if connected && cancellationToken.IsCancellationRequested then
                    // The connect happened to succeed, but the caller's own token fired concurrently —
                    // it still takes priority over a technically-successful result.
                    return Error(ProcessError.Cancelled program)
                elif
                    connected
                    && (linked.Token.IsCancellationRequested || clock.Elapsed >= armedTimeout)
                then
                    // `Task.WhenAny` can still pick `connectTask` as the winner even though the deadline
                    // fired at essentially the same moment (both tasks completing concurrently is a
                    // genuine scheduler race, not just a check-then-act gap) — the unified-deadline
                    // contract requires reporting the stale success as NotReady, not
                    // as a technically-successful `Ok`.
                    return Error(ProcessError.NotReady(program, armedTimeout))
                elif connected then
                    return Ok()
                elif cancellationToken.IsCancellationRequested then
                    return Error(ProcessError.Cancelled program)
                else
                    return Error(ProcessError.NotReady(program, armedTimeout))
        }

    /// Wait until a TCP connection to `endpoint` succeeds, or fail with `NotReady` once the shared
    /// `timeout` deadline elapses (or `Cancelled` if `cancellationToken` fires first). See
    /// `waitForPortUsing` for the full deadline contract (including the ratified scheduler-bounded
    /// window at the deadline); this wires it to a real `TcpClient`. Background-drains (and discards)
    /// the child's piped `stdout`/`stderr` for the duration of the poll — see `withBackgroundDrain`.
    let waitForPort
        (program: string)
        (stdout: Stream option)
        (stderr: Stream option)
        (endpoint: IPEndPoint)
        (timeout: TimeSpan)
        (cancellationToken: CancellationToken)
        : Task<Result<unit, ProcessError>> =
        let tcpConnect (endpoint: IPEndPoint) (ct: CancellationToken) : Task =
            task {
                use client = new TcpClient()
                do! client.ConnectAsync(endpoint.Address, endpoint.Port, ct)
            }

        withBackgroundDrain stdout stderr (fun () ->
            waitForPortUsing tcpConnect program endpoint timeout cancellationToken)

    /// The `waitFor` polling loop itself, unaware of draining — factored out so `waitFor` can wrap it
    /// with `withBackgroundDrain` without duplicating the loop. Shares the exact deadline shape of
    /// `waitForPortUsing`: one `CancellationTokenSource(armedTimeout)` linked with the caller's token, a
    /// single reusable `deadlineSignal`, every `probe` invocation raced against it, backoff capped to the
    /// remaining budget, a non-positive `timeout` returning `NotReady` immediately (with the clamped
    /// value), post-deadline success reported as `NotReady`, and the caller's token winning over a
    /// concurrent success. The same ratified scheduler-bounded window applies: a new `probe` is only
    /// invoked once the loop-top guard confirms the deadline has not been observed to elapse, but a
    /// preemptive runtime may still let at most one invocation begin marginally after the wall-clock
    /// deadline — its late success is reported as `NotReady` and its late fault is safely observed via an
    /// `OnlyOnFaulted` continuation, never surfacing as an unobserved task exception.
    ///
    /// `probe` is invoked through `Task.Run`: a caller-owned `Func<Task<bool>>` has no cancellation seam
    /// of its own, and one that *blocks synchronously* — never even returning a task — would otherwise
    /// pin this loop and defeat the deadline entirely. Isolating the invocation on the thread pool means
    /// even such a probe cannot delay this loop's return past the deadline; the blocked call keeps
    /// running on a pool thread but is abandoned (and its eventual fault observed) exactly like a
    /// returned-but-never-completing task. The API does not claim it can force a caller-owned probe to
    /// stop.
    let private waitForCoreUsing
        (program: string)
        (probe: CancellationToken -> Task<bool>)
        (timeout: TimeSpan)
        (cancellationToken: CancellationToken)
        : Task<Result<unit, ProcessError>> =
        task {
            if cancellationToken.IsCancellationRequested then
                return Error(ProcessError.Cancelled program)
            elif timeout <= TimeSpan.Zero then
                return Error(ProcessError.NotReady(program, timeout))
            else
                // Clamp so an out-of-range timeout can't throw out of the CTS constructor. The clamped
                // value is also what gets reported in NotReady below — an over-long requested timeout is
                // silently capped at ~24.8 days, so reporting the raw, un-clamped value would claim a
                // budget longer than what was actually enforced.
                let armedTimeout = Timeouts.clampArmable timeout
                use timeoutCts = new CancellationTokenSource(armedTimeout)

                use linked =
                    CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, cancellationToken)

                let clock = Stopwatch.StartNew()

                // A standalone deadline signal — a task that only ever completes (cancelled) once
                // `linked` fires — reused across attempts so it can be raced against each `probe` call
                // without rearming a fresh timer every iteration.
                let deadlineSignal = Task.Delay(Timeout.Infinite, linked.Token)

                let mutable ready = false
                let mutable stopped = false

                while not ready && not stopped do
                    if linked.Token.IsCancellationRequested || clock.Elapsed >= armedTimeout then
                        // The shared deadline (or the caller's token) already fired since the last
                        // backoff completed — there is no budget left to invoke `probe` again.
                        // Checked by elapsed time as well as by token, because a `CancellationTokenSource`
                        // timer callback is not guaranteed to have run yet even though its due time has
                        // already passed — relying on the token alone could start one more attempt after
                        // the deadline has, in wall-clock terms, already elapsed.
                        stopped <- true
                    else
                        // Invoke on the thread pool so a probe that blocks *synchronously* (never
                        // returning a task at all) can't pin this loop and defeat the deadline. The
                        // `Task.Run<bool>` overload unwraps the returned `Task<bool>`, so `probeTask`
                        // completes with the probe's own result/fault, just off the calling thread.
                        let probeTask = Task.Run<bool>(fun () -> probe linked.Token)
                        let! winner = Task.WhenAny(probeTask :> Task, deadlineSignal)

                        if obj.ReferenceEquals(winner, deadlineSignal) then
                            // The deadline (or the caller's token) fired before `probe` completed. There
                            // is no way to cancel a caller-owned `Func<Task<bool>>`, so let it keep
                            // running in the background — but observe its eventual outcome so a late
                            // fault is not left as an unobserved task exception.
                            stopped <- true

                            probeTask.ContinueWith(
                                (fun (finished: Task<bool>) -> finished.Exception |> ignore),
                                TaskContinuationOptions.OnlyOnFaulted
                                ||| TaskContinuationOptions.ExecuteSynchronously
                            )
                            |> ignore
                        else
                            // `probeTask` already completed within budget; awaiting it here is
                            // immediate and re-raises its exception, if any, exactly as invoking the
                            // probe directly would.
                            let! result = probeTask

                            if result then
                                ready <- true
                            else
                                // Cap the backoff to whatever budget remains so a fixed 50ms poll can't
                                // overrun a very short overall timeout on its own.
                                let backoff = remainingBackoff clock armedTimeout

                                if backoff <= TimeSpan.Zero then
                                    stopped <- true
                                else
                                    try
                                        do! Task.Delay(backoff, linked.Token)
                                    with :? OperationCanceledException ->
                                        // Deadline/cancellation fired mid-backoff; the loop exits below
                                        // and reports NotReady/Cancelled.
                                        stopped <- true

                if ready && cancellationToken.IsCancellationRequested then
                    // The probe happened to flip true, but the caller's own token fired concurrently —
                    // it still takes priority over a technically-successful result.
                    return Error(ProcessError.Cancelled program)
                elif ready && (linked.Token.IsCancellationRequested || clock.Elapsed >= armedTimeout) then
                    // `Task.WhenAny` can pick `probeTask` as the winner even though the deadline fired at
                    // essentially the same moment (both tasks completing concurrently is a genuine race,
                    // not just a check-then-act gap) — the unified-deadline contract requires reporting
                    // that as NotReady, not as a technically-successful `Ok`.
                    return Error(ProcessError.NotReady(program, armedTimeout))
                elif ready then
                    return Ok()
                elif cancellationToken.IsCancellationRequested then
                    return Error(ProcessError.Cancelled program)
                else
                    return Error(ProcessError.NotReady(program, armedTimeout))
        }

    /// Poll an HTTP endpoint until a response satisfies `isSatisfactory`, or fail with `NotReady` once
    /// the shared `timeout` deadline elapses (or `Cancelled` if `cancellationToken` fires first).
    /// `getResponse` is factored out so tests can exercise the polling contract without depending on a
    /// particular HTTP transport. Network failures are deliberately false results: a refused connection,
    /// DNS failure, or a request cancelled by the shared deadline means the server is not ready yet.
    let internal waitForHttpUsing
        (getResponse: Uri -> CancellationToken -> Task<HttpResponseMessage>)
        (isSatisfactory: Func<HttpResponseMessage, bool>)
        (program: string)
        (uri: Uri)
        (timeout: TimeSpan)
        (cancellationToken: CancellationToken)
        : Task<Result<unit, ProcessError>> =
        let probe (ct: CancellationToken) : Task<bool> =
            task {
                try
                    use! response = getResponse uri ct
                    return isSatisfactory.Invoke response
                with
                | :? HttpRequestException ->
                    // The endpoint is not reachable yet; retry until the shared deadline expires.
                    return false
                | :? OperationCanceledException ->
                    // A request can be cancelled by the caller or shared readiness deadline; the polling
                    // loop classifies that token state as Cancelled or NotReady after this attempt.
                    return false
            }

        waitForCoreUsing program probe timeout cancellationToken

    /// Poll an HTTP endpoint until a response satisfies `isSatisfactory`, or fail with `NotReady` once
    /// the shared `timeout` deadline elapses (or `Cancelled` if `cancellationToken` fires first).
    /// A single client is used for this readiness operation; its own timeout is disabled so every request
    /// is bounded only by the shared readiness deadline passed to `GetAsync`.
    let waitForHttp
        (program: string)
        (stdout: Stream option)
        (stderr: Stream option)
        (uri: Uri)
        (isSatisfactory: Func<HttpResponseMessage, bool>)
        (timeout: TimeSpan)
        (cancellationToken: CancellationToken)
        : Task<Result<unit, ProcessError>> =
        task {
            use client = new HttpClient(Timeout = Timeout.InfiniteTimeSpan)

            return!
                withBackgroundDrain stdout stderr (fun () ->
                    waitForHttpUsing
                        (fun requestUri ct -> client.GetAsync(requestUri, ct))
                        isSatisfactory
                        program
                        uri
                        timeout
                        cancellationToken)
        }

    /// Poll `probe` until it returns true, or fail with `NotReady` once the shared `timeout` deadline
    /// elapses (or `Cancelled` if `cancellationToken` fires first). See `waitForCoreUsing` for the full
    /// deadline contract (including the ratified scheduler-bounded window at the deadline, the
    /// `Task.Run` isolation of a synchronously-blocking probe, and the safe observation of an abandoned
    /// probe's late fault). Background-drains (and discards) the child's piped `stdout`/`stderr` for the
    /// duration of the poll, like `waitForPort` — see `withBackgroundDrain`.
    let waitFor
        (program: string)
        (stdout: Stream option)
        (stderr: Stream option)
        (probe: Func<Task<bool>>)
        (timeout: TimeSpan)
        (cancellationToken: CancellationToken)
        : Task<Result<unit, ProcessError>> =
        withBackgroundDrain stdout stderr (fun () ->
            waitForCoreUsing program (fun _ -> probe.Invoke()) timeout cancellationToken)
