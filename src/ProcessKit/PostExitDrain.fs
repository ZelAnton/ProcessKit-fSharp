namespace ProcessKit

open System
open System.IO
open System.Threading
open System.Threading.Tasks

/// A read-only pass-through over ONE parent-side output pipe that its owning handle can **sever**:
/// once `severed` fires, an in-flight read unwinds and every read — that one and every later one —
/// answers a clean **EOF** (`0`) instead of the bytes a writer might still send. It is the mechanism
/// behind the bounded post-exit output drain (`PostExitDrain` below): the pump reading through this
/// wrapper then ends exactly as it would on a child that closed its stdout, so the capture it was
/// filling stays intact, the tee is still flushed, and no path has to distinguish this ending from an
/// ordinary one.
///
/// Severing is deliberately NOT a stream `Dispose`. Closing the parent read end is the OWNER's job
/// (`RunningHost.Teardown`, which also decides whether the remaining tree is this run's to kill), and
/// a dispose under an in-flight read surfaces as `ObjectDisposedException`/`IOException` — the
/// genuine-vs-teardown-race classification `Pump.genuineReadFault` has to make. An EOF needs no
/// classification at all, which is why the bound reaches for it first and leaves the fd close where
/// it already belongs.
///
/// Costs nothing while it is not severed: the sever token is handed straight to the inner read (every
/// pump in this library reads with `CancellationToken.None`, so no linked source is ever built), and a
/// read that completes synchronously is returned as-is without allocating a continuation. The wrapper
/// owns no resources — the underlying pipe stream is disposed by the run's teardown, exactly as for
/// `Timeouts.ActivityStream`, which wraps the same streams for the idle watchdog.
type internal SeverableStream(inner: Stream, severed: CancellationToken) =
    inherit Stream()

    // The token an inner read is issued with. The caller's own token is almost always `None` (no pump
    // in this library passes one), so the common path is a single already-existing token rather than a
    // per-read linked `CancellationTokenSource`.
    let issueRead (buffer: Memory<byte>) (token: CancellationToken) = inner.ReadAsync(buffer, token)

    // Whether a caught failure is the SEVER's and only the sever's. A cancellation the CALLER asked for
    // stays a cancellation: it is that caller's own signal, and answering it with a silent short read
    // would report a truncated capture as a complete one.
    let severedNotCaller (callerToken: CancellationToken) : bool =
        severed.IsCancellationRequested && not callerToken.IsCancellationRequested

    // How a read the sever aborted comes back is the TRANSPORT's choice, and the transports under these
    // pipes do not agree: a Windows overlapped handle and a POSIX socket-backed pipe unwind a pending
    // read as an `OperationCanceledException`, while a stream layered over a raw fd (and .NET's own
    // `FileStream` on some paths) reports the aborted operation as an `IOException` — the same
    // `ERROR_OPERATION_ABORTED`/`ECANCELED` in a different wrapper. Both mean exactly one thing here:
    // the read WE cut. Answering only the first with EOF would leave the second to surface as a
    // `ProcessError.Io` and fail a verb whose contract is a truncated capture, so both are the sever's
    // EOF. This never swallows a genuine fault a caller could act on: it applies only once this handle
    // has already severed its own read ends, at which point the capture is over and reported incomplete
    // whatever the pipe does next. An `ObjectDisposedException` is included for the narrow race where
    // teardown disposes the stream just after the sever — likewise not a fault to report.
    let isSeverUnwind (ex: exn) (callerToken: CancellationToken) : bool =
        severedNotCaller callerToken
        && match ex with
           | :? OperationCanceledException
           | :? IOException
           | :? ObjectDisposedException -> true
           | _ -> false

    let readSevering (buffer: Memory<byte>) (callerToken: CancellationToken) : ValueTask<int> =
        if severed.IsCancellationRequested then
            // Already severed: answer EOF without touching the pipe at all.
            ValueTask<int> 0
        elif callerToken.CanBeCanceled then
            ValueTask<int>(
                task {
                    use linked = CancellationTokenSource.CreateLinkedTokenSource(severed, callerToken)

                    try
                        return! issueRead buffer linked.Token
                    with ex when isSeverUnwind ex callerToken ->
                        return 0
                }
            )
        else
            let pending = issueRead buffer severed

            if pending.IsCompletedSuccessfully then
                pending
            else
                ValueTask<int>(
                    task {
                        try
                            return! pending
                        with ex when isSeverUnwind ex callerToken ->
                            return 0
                    }
                )

    override _.CanRead = inner.CanRead
    override _.CanSeek = false
    override _.CanWrite = false

    override _.Length =
        raise (NotSupportedException "a severable output stream has no length")

    override _.Position
        with get () = raise (NotSupportedException "a severable output stream is not seekable")
        and set _ = raise (NotSupportedException "a severable output stream is not seekable")

    override _.Flush() = inner.Flush()

    override _.Seek(_, _) =
        raise (NotSupportedException "a severable output stream is not seekable")

    override _.SetLength _ =
        raise (NotSupportedException "a severable output stream has no length")

    override _.Write(_, _, _) =
        raise (NotSupportedException "a severable output stream is read-only")

    // A synchronous read cannot be interrupted once it has entered the OS, so this can only refuse to
    // START one after the sever. Nothing in this library reads these pipes synchronously (every pump
    // goes through `ReadAsync`); the override exists so the wrapper stays a faithful `Stream`.
    override _.Read(buffer, offset, count) =
        if severed.IsCancellationRequested then
            0
        else
            inner.Read(buffer, offset, count)

    override this.ReadAsync(buffer: byte[], offset: int, count: int, cancellationToken: CancellationToken) =
        this.ReadAsync(Memory<byte>(buffer, offset, count), cancellationToken).AsTask()

    override _.ReadAsync(buffer: Memory<byte>, cancellationToken: CancellationToken) =
        readSevering buffer cancellationToken

/// The one **bounded post-exit output drain** contract, shared by every consumer that — with the
/// child's fate ALREADY settled — still has to wait for its own stdout/stderr pumps to reach EOF: the
/// buffered captures, the discard drains behind `WaitAsync`/`ProfileAsync`, the line/chunk/event
/// streaming sessions' shared outcomes, the interactive sessions, and the shared `ExitTask`.
///
/// Why it exists: the parent's read end reaches EOF only when the LAST writer closes it, and the
/// child's own exit closes only the child's copy. A leader that spawned something which inherited its
/// stdout/stderr — a daemonized worker, a `setsid` helper, a shell's background job — leaves the pipe
/// open behind it, so "wait for the pumps" is an unbounded wait on a process this run's caller never
/// asked about, long after the answer (the leader's outcome) is known. Every verb then hangs, and,
/// worse, never reaches its own teardown: the private group that WOULD have killed the remaining tree
/// is never released.
///
/// The contract has three parts, and every consumer applies all three:
///
/// 1. **A short window for the ordinary tail.** The pumps get `budget ()` to finish after the exit
///    wait settles. A child that closed its pipes on the way out is already at EOF, so the window is
///    never armed at all — an ordinary run pays exactly nothing, and a normal short tail (the last
///    lines still in the pipe) drains inside it untouched.
///
/// 2. **Sever, don't abandon.** When the window elapses, the handle severs its own parent read ends
///    (`SeverableStream` above): each pump ends at a clean EOF with everything it had captured, and
///    the verb returns its ALREADY-decided `Outcome` with an honest `Truncated`. The fd close and the
///    decision about the remaining tree stay with the owner's teardown, which runs moments later — so
///    a private group still reaps its descendants and a SHARED group still leaves them to the group.
///
/// 3. **Observe whatever is left.** A read that the OS will not let go of (a POSIX pty master's
///    blocking `read`, which no token can interrupt) can outlive even the sever. Such a pump is
///    handed to `abandon` — never awaited again, but its eventual fault observed — so a late failure
///    cannot surface as an unobserved task exception at finalization (the K-084 rule, applied to an
///    abandoned pump exactly as `PostKillReap` applies it to an abandoned wait).
///
/// Deliberately SEPARATE from the two other bounded windows this library already has, because it
/// answers a different question and must not be confused with either:
///   * `Command.Timeout` bounds the RUN and changes its disposition (`Outcome.TimedOut`). This bound
///     starts only once a disposition exists and never alters it.
///   * `PostKillReap` bounds the wait for a REAP after a hard kill was delivered. This bound is about
///     output, applies with no kill anywhere in sight, and runs after that wait has already answered.
module internal PostExitDrain =

    /// How long the pumps get to reach EOF once the child's fate is settled. The ordinary tail is
    /// already in the pipe and drains in microseconds, so this is a ceiling for the pathological case
    /// — an inherited pipe held open by a descendant — not a latency anyone pays. Matches the
    /// ProcessKit-rs prototype's `PUMP_TEARDOWN` (5s), which bounds the same post-exit pump join
    /// there (`d6f8ed18f408`).
    let DefaultBudget = TimeSpan.FromSeconds 5.0

    /// The largest delay a BCL timer accepts, mirroring `Timeouts.maxArmable`/`PostKillReap`'s own
    /// copy — this module is compiled before `Timeouts`, so it cannot reuse that one.
    let private maxArmable = TimeSpan.FromMilliseconds(float Int32.MaxValue)

    /// Clamp `duration` into the range a BCL timer can be armed with, so a budget can never throw
    /// synchronously on a completion path.
    let armable (duration: TimeSpan) =
        if duration < TimeSpan.Zero then TimeSpan.Zero
        elif duration > maxArmable then maxArmable
        else duration

    /// Test seam: production NEVER assigns this, so `budget ()` is `DefaultBudget` everywhere. A
    /// regression that must not pay the real five seconds sets it (and restores it) around the call,
    /// exactly like `PostKillReap.budgetOverrideForTests`. Tests in this repository run sequentially,
    /// so a single process-wide seam is safe here.
    let mutable budgetOverrideForTests: TimeSpan option = None

    /// The budget in force for this call — the default, or a test seam's override.
    let budget () =
        match budgetOverrideForTests with
        | Some value -> armable value
        | None -> DefaultBudget

    /// Does `drain` settle within `budget`? `true` when it did (the caller then awaits it for its
    /// result/fault exactly as it did before this bound existed), `false` when it did not.
    ///
    /// Deliberately answers a question instead of awaiting: it never touches `drain`'s outcome, so a
    /// pump fault that lands inside the window is still the caller's to observe and re-raise — a
    /// throwing `OnStdoutLine`/tee before the bound must stay the error it always was. An
    /// already-completed drain answers without arming anything, so the ordinary run pays no timer.
    let settlesWithin (budget: TimeSpan) (drain: Task) : Task<bool> =
        if drain.IsCompleted then
            Task.FromResult true
        else
            task {
                use budgetCts = new CancellationTokenSource()
                let expiry = Task.Delay(armable budget, budgetCts.Token)
                let! winner = Task.WhenAny(drain, expiry)

                if obj.ReferenceEquals(winner, drain) then
                    // Cancel the losing timer so it cannot outlive the decided race (the same
                    // discipline `PostKillReap.awaitWithin` applies to its own budget timer).
                    budgetCts.Cancel()
                    return true
                else
                    return false
            }

    /// Give up the right to await `drain` — a pump the sever could not end — while keeping its
    /// eventual fault observed, so it can never surface as an unobserved task exception at
    /// finalization. Purely observational: it reads no result and replaces no exception.
    let abandon (drain: Task) : unit =
        drain.ContinueWith(
            Action<Task>(fun completed -> completed.Exception |> ignore),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default
        )
        |> ignore
