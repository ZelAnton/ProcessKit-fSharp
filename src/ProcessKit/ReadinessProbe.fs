namespace ProcessKit

open System
open System.Diagnostics
open System.IO
open System.Net
open System.Net.Http
open System.Net.Sockets
open System.Threading
open System.Threading.Tasks

/// How many times one readiness run may invoke the condition it was handed. Every readiness verb polls
/// (`PollUntilDeadline`) while the child is still running. The one exception is the re-check
/// `RunningProcess.raceReadinessAgainstExit` runs after the child's exit has won that race: it asks for
/// `Once`, because a child that has exited can publish nothing further, so re-observing an unchanged
/// terminal state would only burn the budget that remains.
[<RequireQualifiedAccess>]
type internal ReadinessAttempts =
    /// Invoke the condition repeatedly, backing off between attempts, until it holds or the deadline
    /// (or the caller's token) ends the run.
    | PollUntilDeadline
    /// Invoke the condition at most once and report that single answer: a `false` ends the run
    /// immediately instead of backing off for another attempt. The deadline and cancellation still
    /// bound that one attempt exactly as they bound a polling one.
    | Once

/// Readiness probes for a started process that never touch its state: they poll an external
/// condition (a reachable TCP endpoint, an arbitrary async predicate) and report through a `Result`,
/// using only the program name for the `NotReady`/`Cancelled` error. Factored out of `RunningProcess`
/// because they share none of its pipe/streaming/exit machinery; the `WaitForPortAsync`/`WaitForAsync`
/// members are thin wrappers that null-check and delegate here, additionally handing over the
/// child's `stdoutStream`/`stderrStream` (or `None`, when a buffered/streaming verb already owns
/// them) for the background drain below.
module internal ReadinessProbe =

    let validateAbsoluteUri (uri: Uri) =
        ArgumentNullException.ThrowIfNull uri

        if not uri.IsAbsoluteUri then
            raise (ArgumentException("The HTTP probe URI must be absolute.", nameof uri))

    /// The one default HTTP health contract shared by readiness and supervisor liveness: every 2xx
    /// response is satisfactory. Kept as a single delegate so the two callers cannot drift and do not
    /// allocate an equivalent closure for every builder call.
    let defaultHttpSuccess =
        Func<HttpResponseMessage, bool>(fun response -> response.IsSuccessStatusCode)

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

    /// The fixed polling backoff between failed attempts in `waitForCoreUsing`.
    let private pollBackoff = TimeSpan.FromMilliseconds 50.0

    /// The backoff to use for the next poll: the smaller of the fixed `pollBackoff` and whatever time
    /// remains before `armedTimeout` elapses on `timeProvider` — `TimeSpan.Zero` once the budget is already
    /// spent. Caps the fixed backoff so it can never itself carry a very short overall `timeout` past
    /// its own budget.
    let private remainingBackoff (timeProvider: TimeProvider) (startedTimestamp: int64) (armedTimeout: TimeSpan) =
        let remaining = armedTimeout - timeProvider.GetElapsedTime(startedTimestamp)

        if remaining <= TimeSpan.Zero then
            TimeSpan.Zero
        else
            min remaining pollBackoff

    /// The single polling/deadline core every readiness probe funnels through: the HTTP
    /// (`waitForHttpUsing`), port (`waitForPortUsing`), Unix domain socket (`waitForSocketUsing`), path
    /// (`waitForPathUsing`), and custom (`waitFor`) probes each express their per-attempt check as a
    /// `probe: CancellationToken -> Task<bool>` (true = ready) and hand it here, so the deadline
    /// mechanics live in exactly one place instead of being hand-synchronised across copies. Polls
    /// `probe` until it returns true, or fails with `NotReady` once the shared
    /// `timeout` deadline elapses (or `Cancelled` if `cancellationToken` fires first). A non-positive
    /// `timeout` is an immediate `NotReady` — `probe` is never invoked. The `timeout` is clamped through
    /// `Timeouts.clampArmable` (an over-long span is capped at ~24.8 days), and that clamped value is
    /// what a resulting `NotReady` reports, so the reported budget never claims more than was enforced.
    ///
    /// Every `probe` invocation and polling backoff shares the same deadline token, so a short overall
    /// `timeout` can never be overrun by a longer fixed per-attempt window: each attempt is *raced*
    /// against a single shared `deadlineSignal` (not merely started with its own timer), so this returns
    /// the instant the remaining budget (or the caller's token) runs out — it does not wait for a
    /// non-cooperative `probe` (one that, like a real in-flight `TcpClient.ConnectAsync`, can ignore its
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
    /// `probe` is invoked through `Task.Run`: a caller-owned probe has no cancellation seam of its own,
    /// and one that *blocks synchronously* — never even returning a task — would otherwise pin this loop
    /// and defeat the deadline entirely. Isolating the invocation on the thread pool means even such a
    /// probe cannot delay this loop's return past the deadline; the blocked call keeps running on a pool
    /// thread but is abandoned (and its eventual fault observed) exactly like a returned-but-never-
    /// completing task. The API does not claim it can force a caller-owned probe to stop.
    ///
    /// `attempts` chooses between the polling contract above (`PollUntilDeadline`) and a single
    /// observation (`Once`): with `Once` a `false` result ends the run at that first answer instead of
    /// backing off for another attempt, so the caller pays one probe invocation, never the whole
    /// remaining budget. Everything else — the deadline guard, the shared `deadlineSignal` race, the
    /// abandoned-attempt fault observation, and the final `Cancelled`/`NotReady`/`Ok` classification —
    /// is identical for both, which is what keeps the post-exit re-check honest about cancellation and
    /// the deadline rather than being a second, hand-rolled probe path.
    let private waitForCoreUsing
        (timeProvider: TimeProvider)
        (program: string)
        (probe: CancellationToken -> Task<bool>)
        (attempts: ReadinessAttempts)
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
                use timeoutCts = new CancellationTokenSource(armedTimeout, timeProvider)

                use linked =
                    CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, cancellationToken)

                let startedTimestamp = timeProvider.GetTimestamp()

                // A standalone deadline signal — a task that only ever completes (cancelled) once
                // `linked` fires — reused across attempts so it can be raced against each `probe` call
                // without rearming a fresh timer every iteration.
                let deadlineSignal = Task.Delay(Timeout.Infinite, linked.Token)

                let mutable ready = false
                let mutable stopped = false

                while not ready && not stopped do
                    if
                        linked.Token.IsCancellationRequested
                        || timeProvider.GetElapsedTime(startedTimestamp) >= armedTimeout
                    then
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
                            elif attempts = ReadinessAttempts.Once then
                                // A single-observation run: the one attempt answered "not ready", and
                                // that answer is the result. Stopping here (rather than backing off for
                                // another attempt) is what keeps the post-exit re-check as cheap as one
                                // probe invocation instead of the whole remaining budget.
                                stopped <- true
                            else
                                // Cap the backoff to whatever budget remains so a fixed 50ms poll can't
                                // overrun a very short overall timeout on its own.
                                let backoff = remainingBackoff timeProvider startedTimestamp armedTimeout

                                if backoff <= TimeSpan.Zero then
                                    stopped <- true
                                else
                                    try
                                        do! Task.Delay(backoff, timeProvider, linked.Token)
                                    with :? OperationCanceledException ->
                                        // Deadline/cancellation fired mid-backoff; the loop exits below
                                        // and reports NotReady/Cancelled.
                                        stopped <- true

                if ready && cancellationToken.IsCancellationRequested then
                    // The probe happened to flip true, but the caller's own token fired concurrently —
                    // it still takes priority over a technically-successful result.
                    return Error(ProcessError.Cancelled program)
                elif
                    ready
                    && (linked.Token.IsCancellationRequested
                        || timeProvider.GetElapsedTime(startedTimestamp) >= armedTimeout)
                then
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

    /// Wait until `connect` succeeds for `endpoint`, or fail with `NotReady` once the shared `timeout`
    /// deadline elapses (or `Cancelled` if `cancellationToken` fires first). A thin adapter over the
    /// shared `waitForCoreUsing` core — see it for the full deadline contract, including the ratified
    /// scheduler-bounded window at the deadline and the safe observation of an abandoned attempt's late
    /// fault. Each poll is a single connect attempt expressed as a `probe` that returns true on a
    /// successful connection and false on any failure: a refused / unreachable endpoint, or a connect
    /// cancelled by the shared deadline, all mean "not open yet", so the core retries until the deadline
    /// and then classifies the token state as `Cancelled` or `NotReady`.
    ///
    /// `connect` is factored out (rather than hard-coding `TcpClient`) so tests can substitute a
    /// deterministically slow stand-in — exercising deadline cancellation of an in-flight attempt without
    /// depending on real network behaviour (e.g. an unassigned TEST-NET-1 address), which varies across
    /// sandboxed CI environments. `waitForPort` below is the production entry point, wired to a real
    /// `TcpClient` and to the background drain.
    let internal waitForPortUsing
        (timeProvider: TimeProvider)
        (connect: IPEndPoint -> CancellationToken -> Task)
        (program: string)
        (endpoint: IPEndPoint)
        (attempts: ReadinessAttempts)
        (timeout: TimeSpan)
        (cancellationToken: CancellationToken)
        : Task<Result<unit, ProcessError>> =
        let probe (ct: CancellationToken) : Task<bool> =
            task {
                try
                    do! connect endpoint ct
                    return true
                with _ ->
                    // ANY connect failure means the port is not open yet — a refused/unreachable
                    // endpoint, or a connect the shared deadline cancelled. Return false so the core
                    // loop retries within budget; it then reads the token state and reports Cancelled or
                    // NotReady. Catching every failure (not just OperationCanceledException) preserves the
                    // original loop's "any other connection failure just means the server is not up yet"
                    // retry semantics, and awaiting `connect` here observes a late fault from an abandoned
                    // attempt so it never surfaces as an unobserved task exception.
                    return false
            }

        waitForCoreUsing timeProvider program probe attempts timeout cancellationToken

    /// Wait until a TCP connection to `endpoint` succeeds, or fail with `NotReady` once the shared
    /// `timeout` deadline elapses (or `Cancelled` if `cancellationToken` fires first). See
    /// `waitForPortUsing`/`waitForCoreUsing` for the full deadline contract (including the ratified
    /// scheduler-bounded window at the deadline); this wires it to a real `TcpClient`. Background-drains
    /// (and discards) the child's piped `stdout`/`stderr` for the duration of the poll — see
    /// `withBackgroundDrain`. Child-exit detection is not done here: `RunningProcess.WaitForPortAsync`
    /// layers it on (racing this against the handle's shared reap-once exit wait), so a probe on a child
    /// that has already exited reports `NotReady` promptly instead of polling out the whole `timeout` —
    /// the same early-exit contract `WaitForHttpAsync`/`WaitForAsync` honour.
    let waitForPort
        (timeProvider: TimeProvider)
        (program: string)
        (stdout: Stream option)
        (stderr: Stream option)
        (endpoint: IPEndPoint)
        (attempts: ReadinessAttempts)
        (timeout: TimeSpan)
        (cancellationToken: CancellationToken)
        : Task<Result<unit, ProcessError>> =
        let tcpConnect (endpoint: IPEndPoint) (ct: CancellationToken) : Task =
            task {
                use client = new TcpClient()
                do! client.ConnectAsync(endpoint.Address, endpoint.Port, ct)
            }

        withBackgroundDrain stdout stderr (fun () ->
            waitForPortUsing timeProvider tcpConnect program endpoint attempts timeout cancellationToken)

    /// Whether this host can dial a Unix domain socket at all. Factored out as a predicate — rather than
    /// inlined as `Socket.OSSupportsUnixDomainSockets` — so a test can force both branches deterministically
    /// regardless of the actual host's real `AF_UNIX` support: every platform this library targets (Windows
    /// 10 1809+, any current Linux, macOS) supports it, so the unsupported branch is rare in practice but
    /// must still fail with a typed `Unsupported`, never dial (and unpredictably fail/hang on) a socket
    /// family the OS doesn't have, nor silently downgrade to some other transport.
    let internal unixDomainSocketsSupported (isSupported: unit -> bool) : Result<unit, ProcessError> =
        if isSupported () then
            Ok()
        else
            Error(ProcessError.Unsupported "WaitForSocketAsync (this host has no AF_UNIX support)")

    /// Wait until `connect` succeeds for the Unix domain socket at `path`, or fail with `NotReady` once the
    /// shared `timeout` deadline elapses (or `Cancelled` if `cancellationToken` fires first). A thin adapter
    /// over the shared `waitForCoreUsing` core — see `waitForPortUsing`/`waitForCoreUsing` for the full
    /// deadline contract. Each poll is a single connect attempt, exactly like `waitForPortUsing`: any
    /// failure (no listener bound at `path` yet, or a connect the shared deadline cancelled) means "not open
    /// yet" and is retried until the deadline. `connect` is factored out so tests can substitute a
    /// deterministically slow stand-in — same rationale as `waitForPortUsing`. `waitForSocket` below is the
    /// production entry point, wired to a real `AddressFamily.Unix` socket and to the background drain.
    let internal waitForSocketUsing
        (timeProvider: TimeProvider)
        (connect: EndPoint -> CancellationToken -> Task)
        (program: string)
        (endpoint: EndPoint)
        (attempts: ReadinessAttempts)
        (timeout: TimeSpan)
        (cancellationToken: CancellationToken)
        : Task<Result<unit, ProcessError>> =
        let probe (ct: CancellationToken) : Task<bool> =
            task {
                try
                    do! connect endpoint ct
                    return true
                with _ ->
                    // ANY connect failure means the socket is not accepting yet — no listener bound at
                    // `path`, or a connect the shared deadline cancelled. Return false so the core loop
                    // retries within budget; mirrors `waitForPortUsing`'s identical rationale.
                    return false
            }

        waitForCoreUsing timeProvider program probe attempts timeout cancellationToken

    /// Wait until a connection to the Unix domain socket at `path` succeeds, or fail with `NotReady` once
    /// the shared `timeout` deadline elapses (or `Cancelled` if `cancellationToken` fires first). See
    /// `waitForSocketUsing`/`waitForCoreUsing` for the full deadline contract; this wires it to a real
    /// `Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified)`. Background-drains (and
    /// discards) the child's piped `stdout`/`stderr` for the duration of the poll, exactly like
    /// `waitForPort` — see `withBackgroundDrain`. Child-exit detection is not done here:
    /// `RunningProcess.WaitForSocketAsync` layers it on (racing this against the handle's shared reap-once
    /// exit wait), the same early-exit contract every other probe honours. Does NOT itself check
    /// `Socket.OSSupportsUnixDomainSockets` — `RunningProcess.WaitForSocketAsync` gates on
    /// `unixDomainSocketsSupported` before ever calling this function, so an unsupported host never reaches
    /// the `AddressFamily.Unix` socket construction below.
    let waitForSocket
        (timeProvider: TimeProvider)
        (program: string)
        (stdout: Stream option)
        (stderr: Stream option)
        (endpoint: EndPoint)
        (attempts: ReadinessAttempts)
        (timeout: TimeSpan)
        (cancellationToken: CancellationToken)
        : Task<Result<unit, ProcessError>> =
        let unixConnect (socketEndpoint: EndPoint) (ct: CancellationToken) : Task =
            task {
                use client =
                    new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified)

                do! client.ConnectAsync(socketEndpoint, ct)
            }

        withBackgroundDrain stdout stderr (fun () ->
            waitForSocketUsing timeProvider unixConnect program endpoint attempts timeout cancellationToken)

    /// Wait until `exists` reports `path` present, or fail with `NotReady` once the shared `timeout`
    /// deadline elapses (or `Cancelled` if `cancellationToken` fires first). A thin adapter over the
    /// shared `waitForCoreUsing` core — see it for the full deadline contract. This is an EXISTENCE
    /// check only, the portable readiness signal used by pidfiles, sentinel files, lock paths, and
    /// daemons that create a Unix-socket pathname before a caller should attempt a richer connection
    /// probe: a file and a directory both count as present, and nothing here inspects size, content, or
    /// whether a writer has finished. A caller that needs "fully written", not merely "created", should
    /// probe that stronger condition itself through `waitFor`/`RunningProcess.WaitForAsync`. `exists` is
    /// factored out (rather than hard-coding `File.Exists`/`Directory.Exists`) so tests can substitute a
    /// deterministic stand-in; `waitForPath` below is the production entry point, wired to the real
    /// filesystem and to the background drain. Any exception `exists` raises (a denied directory, a
    /// transient I/O error) is treated the same as "does not exist yet" and retried until the deadline —
    /// the same "any failure just means not ready yet" rule `waitForPortUsing`/`waitForSocketUsing` apply
    /// to a refused connect — so a lookup race never surfaces as an unhandled fault out of this probe.
    let internal waitForPathUsing
        (timeProvider: TimeProvider)
        (exists: string -> bool)
        (program: string)
        (path: string)
        (attempts: ReadinessAttempts)
        (timeout: TimeSpan)
        (cancellationToken: CancellationToken)
        : Task<Result<unit, ProcessError>> =
        let probe (_ct: CancellationToken) : Task<bool> =
            let found =
                try
                    exists path
                with _ ->
                    // A filesystem lookup failure (permissions, a transient I/O error, a race with a
                    // concurrent rename) means "not there yet", not a probe fault — retried like any
                    // other failed attempt until the shared deadline.
                    false

            Task.FromResult found

        waitForCoreUsing timeProvider program probe attempts timeout cancellationToken

    /// Resolves a caller-supplied `path` against `workingDirectory` exactly the way
    /// `Command.PreferLocal`'s own relative-entry resolution does (`Native.Common.commandContext`):
    /// an absolute `path` is returned unchanged; a relative one is combined with `workingDirectory`
    /// when the command set one, and otherwise left as-is (so it still resolves against THIS
    /// process's own current directory, matching every BCL filesystem call that receives it
    /// unresolved). Factored out so `waitForPath`'s single up-front resolution follows the same rule
    /// as the rest of the codebase instead of inventing its own.
    let private resolvePathAgainstWorkingDirectory (workingDirectory: string option) (path: string) : string =
        if Path.IsPathRooted path then
            path
        else
            match workingDirectory with
            | Some cwd -> Path.Combine(cwd, path)
            | None -> path

    /// Wait until `path` exists on the real filesystem — as a file, a directory, or anything else
    /// `File.Exists`/`Directory.Exists` can observe — or fail with `NotReady` once the shared `timeout`
    /// deadline elapses (or `Cancelled` if `cancellationToken` fires first). See
    /// `waitForPathUsing`/`waitForCoreUsing` for the full deadline contract and the existence-only
    /// contract. A relative `path` resolves against `workingDirectory` (the run's own
    /// `Command.CurrentDir`, i.e. the CHILD's working directory) when one was set — the same rule
    /// `Command.PreferLocal` already applies to its own relative entries — and otherwise resolves
    /// against this process's own current directory, exactly like an unresolved `File.Exists` call
    /// would; the resolution happens once, before polling starts, not per attempt. Background-drains
    /// (and discards) the child's piped `stdout`/`stderr` for the duration of the poll, exactly like
    /// `waitForPort` — see `withBackgroundDrain`. Child-exit detection is not done here:
    /// `RunningProcess.WaitForPathAsync` layers it on (racing this against the handle's shared
    /// reap-once exit wait), the same early-exit contract every other probe honours. An existence check
    /// has no platform precondition (unlike `waitForSocket`'s `AF_UNIX` requirement), so this never
    /// returns `ProcessError.Unsupported`.
    let waitForPath
        (timeProvider: TimeProvider)
        (program: string)
        (stdout: Stream option)
        (stderr: Stream option)
        (workingDirectory: string option)
        (path: string)
        (attempts: ReadinessAttempts)
        (timeout: TimeSpan)
        (cancellationToken: CancellationToken)
        : Task<Result<unit, ProcessError>> =
        let resolvedPath = resolvePathAgainstWorkingDirectory workingDirectory path

        let pathExists (candidate: string) : bool =
            File.Exists candidate || Directory.Exists candidate

        withBackgroundDrain stdout stderr (fun () ->
            waitForPathUsing timeProvider pathExists program resolvedPath attempts timeout cancellationToken)

    /// Poll an HTTP endpoint until a response satisfies `isSatisfactory`, or fail with `NotReady` once
    /// the shared `timeout` deadline elapses (or `Cancelled` if `cancellationToken` fires first).
    /// `getResponse` is factored out so tests can exercise the polling contract without depending on a
    /// particular HTTP transport. Network failures are deliberately false results: a refused connection,
    /// DNS failure, or a request cancelled by the shared deadline means the server is not ready yet.
    let internal waitForHttpUsing
        (timeProvider: TimeProvider)
        (getResponse: Uri -> CancellationToken -> Task<HttpResponseMessage>)
        (isSatisfactory: Func<HttpResponseMessage, bool>)
        (program: string)
        (uri: Uri)
        (attempts: ReadinessAttempts)
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

        waitForCoreUsing timeProvider program probe attempts timeout cancellationToken

    /// Poll an HTTP endpoint through caller-owned `client`. The client is reused for every attempt and is
    /// never disposed or mutated by ProcessKit; each request still receives the shared readiness token.
    let waitForHttpWithClient
        (timeProvider: TimeProvider)
        (program: string)
        (stdout: Stream option)
        (stderr: Stream option)
        (client: HttpClient)
        (uri: Uri)
        (isSatisfactory: Func<HttpResponseMessage, bool>)
        (attempts: ReadinessAttempts)
        (timeout: TimeSpan)
        (cancellationToken: CancellationToken)
        : Task<Result<unit, ProcessError>> =
        withBackgroundDrain stdout stderr (fun () ->
            waitForHttpUsing
                timeProvider
                (fun requestUri ct -> client.GetAsync(requestUri, ct))
                isSatisfactory
                program
                uri
                attempts
                timeout
                cancellationToken)

    /// Poll an HTTP endpoint until a response satisfies `isSatisfactory`, or fail with `NotReady` once
    /// the shared `timeout` deadline elapses (or `Cancelled` if `cancellationToken` fires first).
    /// A single client is used for this readiness operation; its own timeout is disabled so every request
    /// is bounded only by the shared readiness deadline passed to `GetAsync`.
    let waitForHttp
        (timeProvider: TimeProvider)
        (program: string)
        (stdout: Stream option)
        (stderr: Stream option)
        (uri: Uri)
        (isSatisfactory: Func<HttpResponseMessage, bool>)
        (attempts: ReadinessAttempts)
        (timeout: TimeSpan)
        (cancellationToken: CancellationToken)
        : Task<Result<unit, ProcessError>> =
        task {
            use client = new HttpClient(Timeout = Timeout.InfiniteTimeSpan)

            return!
                waitForHttpWithClient
                    timeProvider
                    program
                    stdout
                    stderr
                    client
                    uri
                    isSatisfactory
                    attempts
                    timeout
                    cancellationToken
        }

    /// Poll `probe` until it returns true, or fail with `NotReady` once the shared `timeout` deadline
    /// elapses (or `Cancelled` if `cancellationToken` fires first). See `waitForCoreUsing` for the full
    /// deadline contract (including the ratified scheduler-bounded window at the deadline, the
    /// `Task.Run` isolation of a synchronously-blocking probe, and the safe observation of an abandoned
    /// probe's late fault). Background-drains (and discards) the child's piped `stdout`/`stderr` for the
    /// duration of the poll, like `waitForPort` — see `withBackgroundDrain`. Child-exit detection is not
    /// done here: `RunningProcess.WaitForAsync` layers it on (racing this against the handle's shared
    /// reap-once exit wait), so a probe on a child that has already exited reports `NotReady` promptly
    /// instead of polling out the whole `timeout` — the same early-exit contract
    /// `WaitForHttpAsync`/`WaitForPortAsync` honour.
    let waitFor
        (timeProvider: TimeProvider)
        (program: string)
        (stdout: Stream option)
        (stderr: Stream option)
        (probe: Func<Task<bool>>)
        (attempts: ReadinessAttempts)
        (timeout: TimeSpan)
        (cancellationToken: CancellationToken)
        : Task<Result<unit, ProcessError>> =
        withBackgroundDrain stdout stderr (fun () ->
            waitForCoreUsing timeProvider program (fun _ -> probe.Invoke()) attempts timeout cancellationToken)
