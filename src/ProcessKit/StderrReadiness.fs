namespace ProcessKit

open System
open System.Collections.Generic
open System.Text
open System.Threading
open System.Threading.Tasks

/// Which shape of stderr output a readiness wait consumes.
[<RequireQualifiedAccess>]
type internal StderrReadinessMode =

    /// Complete framed stderr lines only — the diagnostic-stream twin of `WaitForLineAsync`'s stdout
    /// lines, framed under `StderrLineTerminator` and decoded with `StderrEncoding`.
    | Line

    /// The in-flight tail as it grows, whether or not a line terminator ever follows — what a
    /// newline-free prompt (`Password: `) needs, since a line pump holds such a prompt in its assembly
    /// buffer until a terminator (or EOF) finally arrives.
    | Tail

/// One armed stderr readiness wait: the shape it consumes, the caller's predicate, and the completion
/// its verb is awaiting. `RunContinuationsAsynchronously` so a match never runs the waiting verb's
/// continuation inline on the pump's thread (nor under the watch's lock).
[<Sealed>]
type internal StderrReadinessWaiter(mode: StderrReadinessMode, predicate: Func<string, bool>) =

    let completion =
        TaskCompletionSource<string voption>(TaskCreationOptions.RunContinuationsAsynchronously)

    member _.Mode = mode
    member _.Predicate = predicate
    member _.Completion = completion

/// How an arming wait found the watch.
[<RequireQualifiedAccess>]
type internal StderrReadinessStart =

    /// The retained observations already satisfied the predicate; nothing was armed.
    | Matched of string

    /// stderr observation is over (EOF, or the session concluded) and nothing retained matched.
    | Ended

    /// The stderr pump failed; the wait must surface that fault rather than a readiness verdict.
    | Faulted of exn

    /// Armed and now live on the watch.
    | Armed of StderrReadinessWaiter

/// The bounds and defaults of stderr readiness retention.
module internal StderrReadiness =

    /// The ceiling used when the run set no `OutputBufferPolicy.MaxBytes` of its own: 64 KiB, roughly
    /// one OS pipe buffer's worth of stderr, which is far more than any readiness marker or prompt and
    /// small enough to be an unremarkable per-handle cost.
    let defaultRetentionBytes = 64 * 1024

    /// The explicit cap on everything a stderr readiness watch retains. It follows the run's own
    /// `OutputBufferPolicy.MaxBytes` when one is set — the same ceiling that already force-flushes an
    /// unterminated line in the capture paths — so a caller who has bounded this run's output has
    /// bounded its readiness retention with it, and only a run that bounded nothing falls back to the
    /// default above.
    let retentionBytesFor (policy: OutputBufferPolicy) =
        match policy.MaxBytes with
        | Some cap -> cap
        | None -> defaultRetentionBytes

/// The stderr side of the readiness verbs: it watches ONE handle's framed stderr lines and the
/// unterminated tail between them, and lets a wait block until either satisfies a predicate.
///
/// **It observes; it never consumes the stream.** The session's stderr pump is unchanged and stays the
/// single reader of that pipe: every framed line still reaches `Command.OnStderrLine`, the stderr tee
/// and the session's own capture exactly once, so `FinishAsync`'s `Finished.Stderr` is byte-identical
/// whether or not a readiness wait ran. In particular the in-flight tail a partial wait matched is NOT
/// delivered anywhere as a line — it is delivered exactly once, later, as part of the line the pump
/// eventually frames it into (or as the final unterminated line at EOF). A tail is never confused with
/// a complete line, and no other verb can see the same text twice or lose it.
///
/// **What it retains, and what bounds it.** The pump runs whether or not a wait is armed, so this keeps
/// what a wait has not consumed yet: the framed lines observed while nothing was armed, plus the
/// current unterminated tail. Both are capped, together, by `retentionBytes` (see
/// `StderrReadiness.retentionBytesFor`):
///
/// - the TAIL is force-flushed at the cap — offered to the armed waits one last time, then dropped —
///   exactly as `OutputBufferPolicy.MaxBytes` force-flushes an unterminated line in the capture paths,
///   so a child that floods stderr without ever writing a terminator cannot grow this;
/// - the retained LINES drop oldest-first at the cap (`OverflowMode.DropOldest`'s rule: the most recent
///   output survives), keeping at least the newest line even when that one line is itself over the cap.
///
/// Nothing is retained before the first wait arms — the watch is created by that wait — so a handle
/// nobody waits on pays nothing at all.
///
/// **Consumed observations are not re-delivered.** A wait consumes what it is offered, matching or not,
/// exactly as a `WaitForLineAsync` consumer consumes the stdout lines it reads off the channel: the
/// line it matched, and the lines it skipped past on the way, are gone from this watch when the next
/// wait arms. That is the stderr form of the invariant the stdout channel gives by construction.
///
/// **Predicates run on the pump's thread** as each line/tail is framed, so they must be cheap and
/// non-blocking — the same rule a `Command.OnStderrLine` handler follows. Unlike that handler, a
/// throwing predicate is contained: it fails ITS OWN wait with that exception and is dropped, leaving
/// the pump, the capture and every other verb on the handle untouched.
[<Sealed>]
type internal StderrReadinessWatch(retentionBytes: int) =

    // The one lock serializing the pump's observations against the waits arming/leaving. Every field
    // below is read and written under it, and no continuation runs inside it: each waiter's completion
    // source is `RunContinuationsAsynchronously`, so completing one here never runs the waiting verb's
    // code on the pump's thread while this lock is held.
    let gate = obj ()

    // Framed lines observed while NO wait was armed, oldest first, each with its accounted byte cost
    // (its UTF-8 size plus one separator byte — `Pump.LineBuffer`'s convention, so an empty-line flood
    // still costs something and cannot defeat the cap).
    let pendingLines = Queue<struct (string * int)>()
    let mutable pendingBytes = 0

    // The pump's current in-flight tail, as reported to `ObserveTail`. Cleared when the pump frames it
    // into a line, when a tail wait consumes it, and when it reaches the cap.
    let tail = StringBuilder()
    let mutable tailBytes = 0

    let waiters = ResizeArray<StderrReadinessWaiter>()

    // Set once stderr observation is over for this handle: the pump reached EOF, failed, or the
    // session's own outcome concluded past an abandoned pump. `fault` carries the pump's failure when
    // that is why it ended, so a wait surfaces the real error instead of a readiness verdict.
    let mutable ended = false
    let mutable fault: exn option = None

    let lineCost (line: string) = Encoding.UTF8.GetByteCount line + 1

    // Offer `text` to the armed waits of `mode`, in arming order, completing the first whose predicate
    // accepts it. Returns whether one did — the observation is then that wait's, and is not retained.
    // Runs under `gate`.
    let offer (mode: StderrReadinessMode) (text: string) : bool =
        let mutable matched = false
        let mutable index = 0

        while not matched && index < waiters.Count do
            let waiter = waiters[index]

            if waiter.Mode <> mode then
                index <- index + 1
            else
                let mutable accepted = false
                let mutable predicateFault: exn option = None

                try
                    accepted <- waiter.Predicate.Invoke text
                with ex ->
                    // A throwing predicate is the WAIT's failure, not the pump's: fail that one wait
                    // with the exception and drop it, so the stderr pump, the capture and every other
                    // verb on this handle carry on untouched.
                    predicateFault <- Some ex

                match predicateFault with
                | Some ex ->
                    waiters.RemoveAt index
                    waiter.Completion.TrySetException ex |> ignore
                | None ->
                    if accepted then
                        waiters.RemoveAt index
                        waiter.Completion.TrySetResult(ValueSome text) |> ignore
                        matched <- true
                    else
                        index <- index + 1

        matched

    // Retain one framed line for the next wait to read, dropping oldest-first at the cap. Runs under
    // `gate`.
    let retainLine (line: string) =
        let cost = lineCost line
        pendingLines.Enqueue(struct (line, cost))
        pendingBytes <- pendingBytes + cost

        // Keep at least the newest line even when it alone exceeds the cap: dropping it would leave the
        // retention empty AND lose the most recent output, which is the opposite of what a tail-shaped
        // bound is for.
        while pendingBytes > retentionBytes && pendingLines.Count > 1 do
            let struct (_, evicted) = pendingLines.Dequeue()
            pendingBytes <- pendingBytes - evicted

    let clearTail () =
        tail.Clear() |> ignore
        tailBytes <- 0

    // Scan what is retained, oldest first — the framed lines, then (for a TAIL wait) the tail the pump
    // is still assembling — consuming everything up to and including a match. Runs under `gate`, on the
    // ARMING caller's thread, so a throwing predicate simply faults that caller's own wait.
    let scanRetained (mode: StderrReadinessMode) (predicate: Func<string, bool>) : string voption =
        let mutable found = ValueNone

        while found.IsNone && pendingLines.Count > 0 do
            let struct (line, cost) = pendingLines.Dequeue()
            pendingBytes <- pendingBytes - cost

            if predicate.Invoke line then
                found <- ValueSome line

        match found with
        | ValueSome _ -> found
        | ValueNone ->
            // A tail wait also sees what the pump has assembled but not framed yet; a line wait, by
            // definition, does not — that text is not a line until a terminator (or EOF) makes it one.
            if mode = StderrReadinessMode.Tail && tail.Length > 0 then
                let snapshot = tail.ToString()

                if predicate.Invoke snapshot then
                    clearTail ()
                    ValueSome snapshot
                else
                    ValueNone
            else
                ValueNone

    /// The text the pump appended to its in-flight line since the previous report (see
    /// `Pump.ITailObserver`). Offered whole — the accumulated tail, not just the new fragment — to the
    /// armed tail waits, so a prompt split across two reads still matches as one string.
    member _.ObserveTail(text: string) =
        if not (String.IsNullOrEmpty text) then
            lock gate (fun () ->
                if not ended then
                    tail.Append text |> ignore
                    tailBytes <- tailBytes + Encoding.UTF8.GetByteCount text

                    if offer StderrReadinessMode.Tail (tail.ToString()) then
                        // Consumed by that wait: the next tail starts from what arrives after it. The
                        // pump's own assembly buffer is untouched, so this text still reaches the
                        // capture exactly once, later, inside the line it is framed into.
                        clearTail ()
                    elif tailBytes >= retentionBytes then
                        // Force-flushed at the cap, having just been offered — the readiness twin of
                        // `OutputBufferPolicy.MaxBytes`'s in-flight force-flush.
                        clearTail ())

    /// One complete framed stderr line, exactly as the session's capture and `Command.OnStderrLine`
    /// receive it. The in-flight tail is over by definition, so it is cleared here; the tail waits have
    /// already been offered this line's text (the pump reports the tail's remainder immediately before
    /// framing it), which is why it is offered only to the LINE waits now.
    member _.ObserveLine(line: string) =
        lock gate (fun () ->
            if not ended then
                clearTail ()

                // Retention exists for the gap BETWEEN waits. With a wait armed, an unmatched line is
                // consumed by it — the same thing that happens to a stdout line a `WaitForLineAsync`
                // predicate rejects after reading it off the channel.
                let armed = waiters.Count > 0

                if not (offer StderrReadinessMode.Line line) && not armed then
                    retainLine line)

    /// End stderr observation for this handle: the pump reached EOF or failed, or the session's outcome
    /// concluded past a pump the post-exit drain bound had to abandon. Every armed wait is released at
    /// once — with the pump's fault if there was one, else with "no match, the stream is over", which
    /// its verb reports as `NotReady`. Idempotent: the first call decides, so the pump's own ending and
    /// the session outcome's belt-and-braces call cannot fight. Retained lines are deliberately kept:
    /// a wait armed afterwards still reads them before it sees the ending, exactly as a channel reader
    /// drains what is queued before observing completion.
    member _.Complete(pumpFault: exn option) =
        let released =
            lock gate (fun () ->
                if ended then
                    Array.empty
                else
                    ended <- true
                    fault <- pumpFault
                    let snapshot = waiters.ToArray()
                    waiters.Clear()
                    snapshot)

        for waiter in released do
            match pumpFault with
            | Some ex -> waiter.Completion.TrySetException ex |> ignore
            | None -> waiter.Completion.TrySetResult ValueNone |> ignore

    /// Arm a wait: read what is retained first (a channel reader drains its queued items before it can
    /// observe completion, and this is that same order), then either answer immediately or go live.
    member _.TryStart(mode: StderrReadinessMode, predicate: Func<string, bool>) : StderrReadinessStart =
        lock gate (fun () ->
            match scanRetained mode predicate with
            | ValueSome text -> StderrReadinessStart.Matched text
            | ValueNone ->
                if ended then
                    match fault with
                    | Some ex -> StderrReadinessStart.Faulted ex
                    | None -> StderrReadinessStart.Ended
                else
                    let waiter = StderrReadinessWaiter(mode, predicate)
                    waiters.Add waiter
                    StderrReadinessStart.Armed waiter)

    /// Drop `waiter` — its wait was cancelled, timed out, or has already been answered.
    member _.Release(waiter: StderrReadinessWaiter) =
        lock gate (fun () -> waiters.Remove waiter |> ignore)

    /// Wait until a framed stderr line (`Line`) or the unterminated tail (`Tail`) satisfies
    /// `predicate`. `ValueSome text` is the text that matched; `ValueNone` means stderr observation
    /// ended without one (the caller's verb turns that into `NotReady`). The task is CANCELLED when
    /// `cancellationToken` fires — the caller's own deadline is linked into that token, exactly as
    /// `WaitForLineAsync` links its clamped timeout — and FAULTS with the stderr pump's own failure
    /// when that is what ended the stream, so a genuine read/decode/handler fault surfaces as itself
    /// rather than as a spurious readiness timeout.
    member this.WaitAsync
        (mode: StderrReadinessMode, predicate: Func<string, bool>, cancellationToken: CancellationToken)
        : Task<string voption> =
        match this.TryStart(mode, predicate) with
        | StderrReadinessStart.Matched text -> Task.FromResult(ValueSome text)
        | StderrReadinessStart.Ended -> Task.FromResult ValueNone
        | StderrReadinessStart.Faulted ex -> Task.FromException<string voption> ex
        | StderrReadinessStart.Armed waiter ->
            task {
                // Registered AFTER the waiter is armed, so an already-cancelled token resolves it
                // immediately rather than leaving it live on the watch. Disposed with this scope, which
                // also waits out a callback running concurrently.
                use _registration =
                    cancellationToken.Register(fun () ->
                        this.Release waiter
                        waiter.Completion.TrySetCanceled cancellationToken |> ignore)

                try
                    return! waiter.Completion.Task
                finally
                    // The pump removes a waiter it completes, so this covers the other exits —
                    // cancellation, the deadline, a faulting predicate — and is idempotent either way.
                    this.Release waiter
            }
