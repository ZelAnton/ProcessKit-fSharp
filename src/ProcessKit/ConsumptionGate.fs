namespace ProcessKit

open System.Collections.Generic
open System.IO
open System.Threading
open System.Threading.Tasks

/// The single output consumption a `RunningProcess` has been claimed for. Its output pipes are
/// pumped exactly once: a buffered one-shot verb, a stdout-streaming session, a byte-chunk session, or
/// an event-streaming session — never two readers on the same pipe.
[<RequireQualifiedAccess>]
type internal Consumption =
    | Fresh
    | Buffered
    | StdoutStreaming
    | StdoutChunkStreaming
    | EventStreaming
    | Interactive

/// The ONE refusal every lost claim on a handle answers with. Shared by `ConsumptionGate` (which
/// refuses from inside the lock) and by the `RunningProcess` verbs (which turn the same refusal into
/// their own verb-shaped answer: an `Error` for a `Result`-producing verb, an
/// `InvalidOperationException` for one that hands out an enumerator).
module internal ConsumptionRefusal =

    /// The message text every already-consumed refusal on a handle carries, whether it travels as a
    /// `ProcessError.Unsupported` or as an `InvalidOperationException`.
    let message = "this RunningProcess has already been consumed by another verb"

    let error () = ProcessError.Unsupported message

/// The consumption-claim state machine and terminal-wait ledger of ONE `RunningProcess` handle: who
/// owns the output pipes, which one-shot resources (the stdout/chunk enumerators, the interactive
/// stdin writer, each extra fd) have already been handed out, and the two memoized `Task<Outcome>`s
/// every terminal path on the handle shares.
///
/// It owns the one lock that serializes every one of those transitions — claiming the pipes, setting a
/// streaming session up, memoizing the single exit wait, and handing out the interactive stdin. These
/// are once-per-handle setup steps, never a hot path, so a single `Monitor` keeps their check-then-act
/// pairs atomic under concurrent verbs without the subtlety of a field-by-field lock-free scheme. No
/// genuine `await` is ever held across it: a `task { }` built inside the lock returns to the builder
/// (releasing the `Monitor`) at its first real suspension, so only synchronous setup runs under it.
///
/// **Why one gate rather than one lock per concern.** Every claim here is a check-then-act pair whose
/// two halves must not interleave with another verb's: `ExitTask` picks its shared wait BY the current
/// claim, an interactive session starts the shared exit wait while claiming the pipes, and — the
/// subtlest of them — a terminal `FinishAsync` latches the retain-nothing stdout sink and closes the
/// paired claim gate that keeps that drop honest. Splitting these across locks would reintroduce
/// exactly the windows this type exists to close (KB K-163), so the session-setup work each claim
/// needs is passed IN as a callback and runs under this same lock, instead of the lock being handed
/// out.
type internal ConsumptionGate(interactiveStdin: Stream option, extraFdStreams: (int * Stream) list) =

    let gate = obj ()

    // Single-consumption guard: the output pipes are pumped exactly once. A buffered one-shot verb
    // (OutputString/OutputBytes/Wait/Profile) consumes them whole; the streaming verbs form one
    // session (`StdoutStreaming`: StdoutLines/WaitForLine/Finish share the stdout channel;
    // `EventStreaming`: OutputEvents owns the event channel; `Interactive`: a `PtySession` owns the
    // pipes as UNFRAMED text, matched through `ExpectWindow`). A second, different consumer would race
    // two readers on the same pipe — splitting/losing output — so it is refused. Every transition of
    // this field runs under `gate`, so concurrent verbs (or a verb racing `ExitTask`) resolve to
    // exactly one winning consumer rather than both observing `Fresh` and double-pumping.
    let mutable consumption = Consumption.Fresh

    // Whether `StdoutLinesAsync()` — directly, or transitively through either `StdoutJsonLinesAsync`
    // overload, which both fold into it — has already handed out its one enumerator. Deliberately a
    // SEPARATE flag from `consumption`/`TryClaimStdoutStreaming`'s reentrant-by-design gate below: that
    // gate must stay reentrant so `FinishAsync`/`WaitForLineAsync` can rejoin an already-claimed stdout
    // session as companions, but the enumerator-producing call itself — `StdoutLinesAsync`/
    // `StdoutJsonLinesAsync` — still must not silently hand out a second, redundant reader over the
    // same channel (calling one after the other, or the same one twice). Set only from
    // `TryTakeStdoutLinesEnumerator` below, which `StdoutLinesAsync()` reaches only AFTER
    // `TryClaimStdoutStreaming` has already succeeded, so a handle that never gets past that gate (a
    // buffered/event-streaming verb claimed first) never has this flag poisoned by an attempt that was
    // refused for an unrelated reason.
    let mutable stdoutLinesClaimed = false

    // The chunk-streaming analogue of `stdoutLinesClaimed`: the session setup is deliberately
    // reentrant for `FinishAsync`/`ExitTask`, but the public enumerator is handed out only once.
    let mutable stdoutChunksClaimed = false

    // Latched by the TERMINAL `FinishAsync` when it takes over a stdout LINE-streaming session whose one
    // enumerator was never handed out — the "fresh handle, nobody ever streamed" shape `FinishAsync`
    // itself starts the session for, and equally the `WaitForLineAsync`-started session no
    // `StdoutLinesAsync`/`StdoutJsonLinesAsync` caller ever took over. `FinishAsync` returns the outcome
    // and the captured STDERR; stdout is not part of `Finished` at all, so queueing the child's stdout
    // into the line channel only pins the whole output of a multi-gigabyte producer in memory (the
    // channel is unbounded unless `Command.StreamBuffer` opts in) until the handle is disposed, for
    // output the caller has just declined to take (T-357).
    //
    // The latch turns BOTH ends of that channel off together, under THIS gate's lock, and it is the
    // second half that makes the first one honest:
    //   - the SINK (`OutputSessions`, which reads the latch through the `discarding` callback it is
    //     built with): each framed line is dropped instead of queued. The rest of the pump path is
    //     unchanged — `OnStdoutLine`, `StdoutTee`, `StdoutLineCount`, the decoder, and every genuine
    //     read/handler fault behave exactly as on the streamed path.
    //   - the claim GATE (`TryClaimStdoutStreaming` below): every later NON-terminal stdout-line caller
    //     (`StdoutLinesAsync`/`StdoutJsonLinesAsync`, `WaitForLineAsync`) is refused with the same
    //     already-consumed error a call after `WaitAsync`/`ProfileAsync` gets. Refusing is the point:
    //     handing out a reader over a channel that is no longer being filled would answer
    //     `StdoutLinesAsync` with a silently empty stream and `WaitForLineAsync` with a `NotReady` that
    //     reads as "the line never came" — a silent downgrade of a result these verbs used to deliver,
    //     where this library owes a loud, typed refusal.
    // Together they publish the same retain-nothing stdout contract `WaitAsync`/`ProfileAsync` already
    // do: stdout is drained so the child keeps moving, kept nowhere, and asked for afterwards it is an
    // error rather than an empty answer. Decided under this lock (where `stdoutLinesClaimed` is decided
    // too) and read by the pump on every line, hence `Volatile`.
    //
    // KB K-163: latch and paired gate close TOGETHER, here, under the SAME lock — a fix that moved only
    // the pump's sink once left the gate stale-permissive, and a late `StdoutLinesAsync`/
    // `WaitForLineAsync` then passed it and got an empty stream / a misleading `NotReady` instead of the
    // refusal. `TryTakeStdoutLinesEnumerator` re-checks the latch in its own (second) lock acquisition
    // for the same reason.
    let mutable stdoutStreamDiscarding = false

    // The event-streaming analogue of `stdoutLinesClaimed`/`stdoutChunksClaimed`. Unlike stdout
    // line/chunk streaming, `TryClaimEventStreaming` has no companion verb that needs to rejoin an
    // already-claimed session (`ExitTask`/`StopAsync` reuse the event session's outcome directly, never
    // the claim itself), so this flag lives right in that claim below instead of in a separate
    // enumerator guard. Set only once, the moment the event session is first claimed.
    let mutable eventStreamClaimed = false

    // The once-only interactive-stdin claim, shared by BOTH ways this run's kept-open stdin can find an
    // owner: `TakeStdin`/`TakeStdinAsync` (the caller takes the writer) and `FinishUnclaimedStdin`
    // (a terminal verb ends the child's input because nobody took it). Taken under this lock, and the
    // one flag serves both, so two concurrent claimants — of either kind — can never both observe it
    // unset: whoever wins sets it, every later claim answers `None`. That is what makes `TakeStdin`
    // racing a terminal verb resolve to EXACTLY ONE owner of the pipe, with no double close and no
    // kept-open pipe abandoned behind a lost race.
    let mutable stdinTaken = false

    // The parent ends of the run's `Command.ExtraFd` channels, each handed out at most once.
    let extraFds = Dictionary<int, Stream>()

    let mutable exitStarted = false
    let mutable exitTaskValue = Unchecked.defaultof<Task<Outcome>>

    // A buffered verb's single exit wait (`OutputStringAsync`/`OutputBytesAsync`/`WaitAsync`/
    // `ProfileAsync`, via `EnsureBufferedWait`). `EnsureExitTask` reuses it for an already-`Buffered`
    // handle — the "verb, then WaitAny/WaitAll" order — so it does not start a second `host.Wait()`
    // racing the verb's own, mirroring the streaming sessions' memoized outcomes.
    let mutable bufferedOutcome = Unchecked.defaultof<Task<Outcome>>

    do
        for targetFd, stream in extraFdStreams do
            extraFds.Add(targetFd, stream)

    /// Whether the terminal `FinishAsync` has latched the retain-nothing stdout sink. Read by the line
    /// pump on every framed line (hence the `Volatile` read), and by the claim paths below.
    member _.DiscardingStdoutStream = Volatile.Read(&stdoutStreamDiscarding)

    /// Whether no verb has claimed the output pipes yet. A snapshot read (NOT a claim: `consumption` is
    /// left untouched, so a real verb can still claim the pipes normally afterwards), taken by the
    /// readiness probes before handing their background drain this handle's still-unowned pipes — the
    /// same narrow race window every other snapshot-then-act check on this handle accepts (concurrently
    /// calling two verbs on one handle from different threads without WaitAny/WaitAll is already
    /// undefined elsewhere in this API).
    member _.IsFresh = lock gate (fun () -> consumption = Consumption.Fresh)

    /// Claim the pipes for a one-shot buffered verb — atomically, only from fresh (no re-entry: a
    /// second buffered verb would re-pump already-torn-down streams). Two concurrent buffered verbs
    /// therefore resolve to exactly one winner; the loser is refused.
    member _.TryClaimBuffered() =
        lock gate (fun () ->
            if consumption = Consumption.Fresh then
                consumption <- Consumption.Buffered
                true
            else
                false)

    /// Claim (or rejoin) the stdout LINE-streaming session, running `startSession` — the pumps, the
    /// stderr capture and the session's combined outcome — under this same lock on the ONE call that
    /// claims it from fresh. So a concurrent second `StdoutLinesAsync`/`WaitForLineAsync`/`FinishAsync`
    /// either observes a fully-constructed session or, if it is an incompatible consumer, is atomically
    /// refused — never a half-built session, and never two racing setups building two readers on the one
    /// channel.
    ///
    /// `terminalOnly` marks the ONE caller that ends the run rather than consuming its stdout —
    /// `FinishAsync`. Reaching here from there with no enumerator handed out (`stdoutLinesClaimed`)
    /// makes the session a retain-nothing drain instead of a queue nobody will empty, and closes this
    /// gate to every later non-terminal stdout-line caller — so "nothing can ever read the line channel
    /// again" is enforced right here rather than merely assumed; see `stdoutStreamDiscarding` above.
    /// The decision is made HERE, under the same lock and before `startSession` builds the pumps, so the
    /// fresh case never queues even a first line; deciding it afterwards would leave a window in which
    /// the pump had already started filling the channel.
    ///
    /// Returns false when a different consumption (a buffered verb, or event streaming) already owns the
    /// pipes, and — for a non-terminal caller — once a terminal `FinishAsync` has discarded this
    /// session's stdout; true once the stdout streaming session is (or already was) ours.
    member _.TryClaimStdoutStreaming(terminalOnly: bool, startSession: unit -> unit) =
        lock gate (fun () ->
            // A stream that was handed out (`stdoutLinesClaimed`) keeps the existing join semantics in
            // full: `FinishAsync` is then the terminal hand-off AFTER streaming, and its caller may still
            // be holding — or about to drain — the enumerator, so every line stays queued exactly as
            // before. (Racing `StdoutLinesAsync` against `FinishAsync` from two threads resolves to one
            // of the two orders under this lock; concurrent verbs on one handle are undefined elsewhere
            // in this API for the same reason.)
            let latchTerminalDiscard () =
                if terminalOnly && not stdoutLinesClaimed then
                    Volatile.Write(&stdoutStreamDiscarding, true)

            if not terminalOnly && Volatile.Read(&stdoutStreamDiscarding) then
                // A terminal `FinishAsync` already latched the retain-nothing sink for a stream nobody
                // took, so this channel is no longer being filled and never will be again. Refuse the
                // caller with the ordinary already-consumed error — the same answer it would get after
                // `WaitAsync`/`ProfileAsync`, which retain nothing either — rather than hand out a reader
                // that could only report an empty stdout the child never had. `FinishAsync` itself
                // (`terminalOnly`) still passes: repeating it stays the idempotent terminal hand-off it
                // is for a streamed session.
                false
            elif consumption = Consumption.StdoutStreaming then
                latchTerminalDiscard ()
                true
            elif consumption <> Consumption.Fresh then
                false
            else
                latchTerminalDiscard ()
                consumption <- Consumption.StdoutStreaming
                startSession ()
                true)

    /// Hand out the stdout line session's ONE enumerator, or refuse. Called by `StdoutLinesAsync` after
    /// `TryClaimStdoutStreaming` has already succeeded — so this is that verb's SECOND acquisition of
    /// this lock, and the latch is deliberately re-checked here as well: a terminal `FinishAsync` racing
    /// in between still sees no enumerator handed out and latches the retain-nothing sink, so the stream
    /// this call is about to return would quietly run dry. Concurrent verbs on one handle are undefined
    /// API-wide, but "undefined" must not mean "silently empty" — refuse with the same already-consumed
    /// error the gate itself would have (KB K-163).
    member _.TryTakeStdoutLinesEnumerator() =
        lock gate (fun () ->
            if stdoutLinesClaimed then
                // `TryClaimStdoutStreaming` is deliberately reentrant (it must let `FinishAsync`/
                // `WaitForLineAsync` rejoin an already-claimed session), so it alone can't refuse this
                // second enumerator-producing call — that is what this flag is for.
                false
            elif Volatile.Read(&stdoutStreamDiscarding) then
                false
            else
                stdoutLinesClaimed <- true
                true)

    /// The byte-chunk counterpart of `TryClaimStdoutStreaming`, with the same atomic check + claim +
    /// setup and the same reentrancy for `FinishAsync`/`ExitTask`.
    ///
    /// Needs no `terminalOnly` discard counterpart (T-357): this session has no "fresh, nobody took the
    /// stream" shape to protect against. `StdoutChunksAsync` — the verb that hands out the enumerator —
    /// is the only caller that can start it from `Fresh`; `FinishAsync`/`ExitTask` reach here only once
    /// `consumption` is ALREADY `StdoutChunkStreaming` (on a fresh handle the line-streaming claim wins
    /// ahead of them), i.e. only after a caller took the chunk enumerator. An enumerator taken and then
    /// abandoned keeps its queued chunks by the same rule the line path applies to a handed-out stream.
    member _.TryClaimStdoutChunkStreaming(startSession: unit -> unit) =
        lock gate (fun () ->
            if consumption = Consumption.StdoutChunkStreaming then
                true
            elif consumption <> Consumption.Fresh then
                false
            else
                consumption <- Consumption.StdoutChunkStreaming
                startSession ()
                true)

    /// Hand out the chunk session's ONE enumerator, or refuse — the chunk analogue of
    /// `TryTakeStdoutLinesEnumerator` (no latch to re-check: the chunk session has no discard path).
    member _.TryTakeStdoutChunksEnumerator() =
        lock gate (fun () ->
            if stdoutChunksClaimed then
                false
            else
                stdoutChunksClaimed <- true
                true)

    /// Claim the event-streaming session, running `startSession` under this lock on the ONE call that
    /// claims it. Returns false when a different consumption (a buffered verb, or stdout streaming)
    /// already owns the pipes, OR when the event session itself was already claimed by an earlier
    /// `OutputEventsAsync()` call; true only for the ONE call that first claims it.
    ///
    /// Needs no `terminalOnly` discard counterpart either (T-357): `OutputEventsAsync()` — the verb that
    /// hands out the enumerator — is this session's ONLY entry point, so an event channel can never be
    /// filled for a consumer that does not exist. No terminal verb starts or rejoins it: `FinishAsync`
    /// goes through the stdout claims above (and is refused outright once `EventStreaming` owns the
    /// pipes), and `ExitTask`/`StopAsync` reuse the session's outcome directly without re-entering here.
    member _.TryClaimEventStreaming(startSession: unit -> unit) =
        lock gate (fun () ->
            if consumption = Consumption.EventStreaming then
                // The event channel is created with `SingleReader = true` (`StreamChannel.create`), so a
                // second concurrent reader relies on undefined behaviour of a single-consumer-optimized
                // channel — refuse it instead of reentrantly handing out a second enumerator. No internal
                // caller re-enters this claim to rejoin an already-claimed session (`ExitTask`/
                // `StopAsync` reuse the session outcome directly), so this branch only ever serves a
                // repeat `OutputEventsAsync()` call.
                if eventStreamClaimed then
                    false
                else
                    eventStreamClaimed <- true
                    true
            elif consumption <> Consumption.Fresh then
                false
            else
                consumption <- Consumption.EventStreaming
                eventStreamClaimed <- true
                startSession ()
                true)

    /// Claim the pipes for an interactive session (`PtySession`, `ContentLengthSession`) by running
    /// `startSession` under this lock. Unlike the streaming claims this is deliberately NOT reentrant: a
    /// second session over one handle would give two matchers one window, each silently consuming the
    /// other's output, so it is refused with the same already-consumed error every other verb reports.
    ///
    /// The claim is committed only once `startSession` reports `Ok`, so a session that refuses itself
    /// for its OWN reason (`ContentLengthSession` on a run with no piped stdout) leaves the handle
    /// `Fresh` and still claimable, exactly as before this gate existed — while an already-claimed
    /// handle is refused here without `startSession` running at all, so the already-consumed answer
    /// still outranks the session's own precondition.
    member _.TryClaimInteractive(startSession: unit -> Result<'a, ProcessError>) : Result<'a, ProcessError> =
        lock gate (fun () ->
            if consumption <> Consumption.Fresh then
                Error(ConsumptionRefusal.error ())
            else
                match startSession () with
                | Ok value ->
                    consumption <- Consumption.Interactive
                    Ok value
                | Error err -> Error err)

    /// The once-only interactive-stdin claim (see `stdinTaken`). `Some` exactly for the FIRST claimant
    /// of a run that kept stdin open; `None` for every later one and for a run that keeps no writer.
    /// Deliberately claims WITHOUT waiting for the source feeder: that wait must happen outside this
    /// lock, and each caller picks how to serve it.
    member _.TryClaimInteractiveStdin() : Stream option =
        lock gate (fun () ->
            match interactiveStdin with
            | Some stream when not stdinTaken ->
                stdinTaken <- true
                Some stream
            | _ -> None)

    /// Take the parent side of the full-duplex channel connected to `targetFd` in the child — `Some`
    /// only for a descriptor configured with `Command.ExtraFd`, and only once.
    member _.TryTakeExtraFd(targetFd: int) : Stream option =
        lock gate (fun () ->
            match extraFds.TryGetValue targetFd with
            | true, stream ->
                extraFds.Remove targetFd |> ignore
                Some stream
            | false, _ -> None)

    /// Start (and memoize) a buffered verb's single exit wait. Every buffered verb goes through this
    /// instead of creating its own wait; the first caller creates it, and both the verb that owns the
    /// pipes and a concurrent `ExitTask` on the same handle (the "verb, then WaitAny/WaitAll" order)
    /// share that one wait — one `host.Wait()`, one set of readers — with correct cross-thread
    /// visibility in either arrival order.
    ///
    /// `create` runs at most once, under this lock, and is where the caller attaches its
    /// fault-observation exactly once at creation (KB K-084): a readiness probe races this wait without
    /// ever awaiting it, so a fault on it would otherwise surface as an unobserved task exception at
    /// finalization on a probe-only handle.
    ///
    /// Reentrant on this lock by design: a claim callback (an interactive session's setup, `ExitTask`'s
    /// own selection) legitimately reaches this while already holding it.
    member _.EnsureBufferedWait(create: unit -> Task<Outcome>) : Task<Outcome> =
        lock gate (fun () ->
            if obj.ReferenceEquals(bufferedOutcome, null) then
                bufferedOutcome <- create ()

            bufferedOutcome)

    /// The memoized exit task behind `WaitAnyAsync`/`WaitAllAsync`: built exactly once under this lock
    /// (so concurrent racers on the same handle can't create two racing waits), reusing whichever
    /// consumption already owns the pipes instead of ever starting a second reader.
    ///
    /// `forClaimedPipes` is asked for the outcome to share when a verb/session already owns the pipes —
    /// the session's own combined outcome, or (for `Buffered`) the memoized `EnsureBufferedWait`.
    /// `claimFreshForExit` runs only when nothing has claimed them yet: the buffered slot is claimed
    /// HERE first (inline, under this lock) so a terminal verb called after WaitAny/WaitAll on the same
    /// handle is refused rather than racing a second reader on these pipes, and only then is its own
    /// drain-and-wait built.
    member _.EnsureExitTask
        (forClaimedPipes: Consumption -> Task<Outcome>, claimFreshForExit: unit -> Task<Outcome>)
        : Task<Outcome> =
        lock gate (fun () ->
            if not exitStarted then
                exitStarted <- true

                exitTaskValue <-
                    if consumption = Consumption.Fresh then
                        consumption <- Consumption.Buffered
                        claimFreshForExit ()
                    else
                        forClaimedPipes consumption

            exitTaskValue)
