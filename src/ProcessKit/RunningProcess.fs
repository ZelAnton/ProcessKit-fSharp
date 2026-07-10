namespace ProcessKit

open System
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.Net
open System.Runtime.ExceptionServices
open System.Runtime.InteropServices
open System.Threading
open System.Threading.Tasks
open System.Threading.Channels

/// The closures and state a `RunningProcess` is built from. Internal — `ProcessGroup.StartAsync`
/// constructs it, so `RunningProcess` need not reference `ProcessGroup` (no compile cycle).
type internal RunningHost =
    {
        Config: CommandConfig
        Pid: int option
        Stdout: Stream option
        Stderr: Stream option
        Stdin: Stream option
        StartTime: DateTime
        StartedTimestamp: int64
        /// Wait for the process to exit and report how it concluded.
        Wait: unit -> Task<Outcome>
        /// Observe a genuine stdin-source failure stashed by the background feeder, but only once the
        /// feed has finished — a still-running feed yields `None` and never blocks. A Result-producing
        /// verb surfaces it as `ProcessError.Stdin`, but only on an otherwise-successful run.
        StdinError: unit -> exn option
        /// Signal the tree to die without waiting (start_kill).
        StartKill: unit -> unit
        /// Gracefully kill the tree (SIGTERM, then SIGKILL after the grace period) without
        /// releasing the container — for timeouts.
        GracefulKill: TimeSpan -> Task
        /// Reap the tree and release the container.
        Teardown: unit -> ValueTask
    }

/// The result of `RunningProcess.WaitAnyAsync`: which started process finished first and how it
/// concluded. A named type (rather than a tuple) so the fields read clearly from C#.
[<Sealed; NoComparison>]
type WaitAnyResult internal (index: int, outcome: Outcome) =

    /// The index, into the array passed to `WaitAnyAsync`, of the process that finished first.
    member _.Index = index

    /// How that process concluded.
    member _.Outcome = outcome

/// The single output consumption a `RunningProcess` has been claimed for. Its output pipes are
/// pumped exactly once: a buffered one-shot verb, a stdout-streaming session, or an event-streaming
/// session — never two readers on the same pipe.
[<RequireQualifiedAccess>]
type internal Consumption =
    | Fresh
    | Buffered
    | StdoutStreaming
    | EventStreaming

/// A live handle to a started process: stream its output, feed its stdin, wait for it, or
/// collect it to completion. Disposing it reaps the whole process tree (kill-on-drop).
[<Sealed>]
type RunningProcess internal (host: RunningHost) =

    let config = host.Config

    // The one lock that serializes every transition of this handle's consumption state machine —
    // claiming the pipes (`claimBuffered`, the streaming-session setup), memoizing the single exit
    // wait (`ensureBufferedWait`, `ExitTask`), and handing out the interactive stdin (`TakeStdin`).
    // These are once-per-handle setup steps, never a hot path, so a single Monitor keeps their
    // check-then-act pairs atomic under concurrent verbs without the subtlety of a field-by-field
    // lock-free scheme. No genuine `await` is ever held across it: a `task { }` built inside the lock
    // returns to the builder (releasing the Monitor) at its first real suspension, so only synchronous
    // setup runs under it.
    let stateLock = obj ()

    let mutable stdinTaken = false
    let mutable stdoutLineCount = 0
    let mutable stderrLineCount = 0
    let mutable droppedStreamLineCount = 0
    let mutable stderrStreamBuffer = Unchecked.defaultof<Pump.LineBuffer>
    let mutable streamOutcome = Unchecked.defaultof<Task<Outcome>>

    let bumpDroppedStreamLine () =
        Interlocked.Increment(&droppedStreamLineCount) |> ignore

    // `stdoutLineCount`/`stderrLineCount` are written by a background pump task and read from the
    // consumer's thread via `StdoutLineCount`/`StderrLineCount` and the `countSoFar` callbacks below —
    // `Interlocked.Increment` to publish each write, `Volatile.Read` (see the two members) to read a
    // fresh value, the same atomic approach `droppedStreamLineCount` already uses.
    let bumpStdoutLine () =
        Interlocked.Increment(&stdoutLineCount) |> ignore

    let bumpStderrLine () =
        Interlocked.Increment(&stderrLineCount) |> ignore

    let readStdoutLineCount () = Volatile.Read(&stdoutLineCount)
    let readStderrLineCount () = Volatile.Read(&stderrLineCount)

    // Idle-timeout (`Command.IdleTimeout`, opt-in): a resettable "no output" watchdog, plus thin
    // activity-tracking wrappers around the stdout/stderr pipes that reset it on every non-empty read.
    // Byte granularity — honest and uniform across every verb (line pumps, byte drains, raw captures
    // all reset it), and independent of the line counters above. Unset (the default): no timer, and the
    // raw pipe streams pass straight through with zero overhead, keeping the idle path entirely opt-in.
    // Armed by `waitWithTimeout` (via `Timeouts.raceTimeout`) when the exit wait begins; disposed with
    // this handle.
    let idleTimer: Timeouts.IdleTimer option =
        match config.IdleTimeout with
        | Some idle when Timeouts.isArmable idle -> Some(new Timeouts.IdleTimer(idle))
        | _ -> None

    let watchActivity (stream: Stream option) : Stream option =
        match idleTimer with
        | Some timer ->
            stream
            |> Option.map (fun s -> new Timeouts.ActivityStream(s, timer.Reset) :> Stream)
        | None -> stream

    let stdoutStream = watchActivity host.Stdout
    let stderrStream = watchActivity host.Stderr

    // Cancels a writer parked on a bounded stream's `StreamFullMode.Backpressure` (`WriteAsync`) once
    // this handle is torn down, so an abandoned bounded stream can't leave its pump running forever: a
    // `Command.Timeout` kills the CHILD but does not by itself free a writer waiting here if nothing
    // ever reads again (see the deadlock note in docs/streaming.md). No `CancelAfter` is ever armed on
    // it, so it owns no timer — there is nothing to release, and skipping `Dispose` is safe.
    let disposalCts = new CancellationTokenSource()

    // The streaming channels and their policy-aware writer live in `StreamChannel`: the stdout channel
    // is written by exactly one pump, the event channel by two (stdout + stderr), and either is bounded
    // when `config.StreamBuffer` opts in (else unbounded, as before).
    let stdoutChannel: Channel<string> = StreamChannel.create config.StreamBuffer true

    let eventChannel: Channel<OutputEvent> =
        StreamChannel.create config.StreamBuffer false

    // Write one item to a streaming channel per `config.StreamBuffer` (see `StreamChannel.writeItem`):
    // unbounded `TryWrite` when unset, else backpressure / drop / fail-loud. Bound `Backpressure` to
    // `disposalCts.Token` so an abandoned bounded stream's writer can't outlive this handle.
    let writeStreamItem
        (writer: ChannelWriter<'T>)
        (reader: ChannelReader<'T>)
        (countSoFar: unit -> int)
        (onDrop: unit -> unit)
        (item: 'T)
        : ValueTask =
        StreamChannel.writeItem
            config.StreamBuffer
            config.Program
            disposalCts.Token
            writer
            reader
            countSoFar
            onDrop
            item

    let mutable exitStarted = false
    let mutable exitTaskValue = Unchecked.defaultof<Task<Outcome>>

    // 0 = no `StopAsync` has fired the soft-kill yet; `Interlocked.Exchange` flips it to 1 for the
    // first one. A repeat `StopAsync` (or one racing a `Dispose` that already reaped the container)
    // then skips re-entering the native graceful kill on an already-released container and only awaits
    // the same exit outcome — the once-guard that makes `StopAsync` idempotent.
    let mutable stopStarted = 0

    // The event-streaming session's single combined outcome (waiting for exit + draining both pipes via
    // the two pumps). ExitTask reuses it for an `EventStreaming` handle so it does not start a second,
    // racing set of drains on the same streams.
    let mutable eventOutcome = Unchecked.defaultof<Task<Outcome>>

    // A buffered verb's single exit wait (`OutputStringAsync`/`OutputBytesAsync`/`WaitAsync`/
    // `ProfileAsync`, via `ensureBufferedWait`). ExitTask reuses it for an already-`Buffered` handle —
    // the "verb, then WaitAny/WaitAll" order — so it does not start a second `host.Wait()` racing the
    // verb's own, mirroring `streamOutcome`/`eventOutcome` above for the streaming sessions.
    let mutable bufferedOutcome = Unchecked.defaultof<Task<Outcome>>

    // Set by the winning total/idle deadline before teardown begins, then threaded into buffered
    // ProcessResult construction. Duration remains the complete wall-clock elapsed time.
    let mutable configuredTimeoutDuration: TimeSpan option = None

    // Single-consumption guard: the output pipes are pumped exactly once. A buffered one-shot verb
    // (OutputString/OutputBytes/Wait/Profile) consumes them whole; the streaming verbs form one
    // session (`StdoutStreaming`: StdoutLines/WaitForLine/Finish share the stdout channel;
    // `EventStreaming`: OutputEvents owns the event channel). A second, different consumer would race
    // two readers on the same pipe — splitting/losing output — so it is refused. Every transition of
    // this field runs under `stateLock`, so concurrent verbs (or a verb racing `ExitTask`) resolve to
    // exactly one winning consumer rather than both observing `Fresh` and double-pumping.
    let mutable consumption = Consumption.Fresh

    // Claim the pipes for a one-shot buffered verb — atomically, only from fresh (no re-entry: a
    // second buffered verb would re-pump already-torn-down streams). Two concurrent buffered verbs
    // therefore resolve to exactly one winner; the loser is refused (`alreadyConsumedError`).
    let claimBuffered () =
        lock stateLock (fun () ->
            if consumption = Consumption.Fresh then
                consumption <- Consumption.Buffered
                true
            else
                false)

    let alreadyConsumedMessage =
        "this RunningProcess has already been consumed by another verb"

    let alreadyConsumedError () =
        ProcessError.Unsupported alreadyConsumedMessage

    // Hand `stdoutStream`/`stderrStream` to a readiness probe (`WaitForPortAsync`/`WaitForAsync`) for
    // its background drain — but only a still-`Fresh` handle's pipes: if a buffered verb or a
    // streaming session already claimed them, that consumer's own pump already drains them, and
    // handing the same streams to the probe as well would start a second, racing reader on the same
    // pipe. A snapshot read (not a claim: `consumption` is left untouched, so a real verb can still
    // claim the pipes normally once the probe stops draining) taken once, before the probe's first
    // attempt — the same narrow race window every other snapshot-then-act check in this class
    // accepts (concurrently calling two verbs on one handle from different threads without
    // WaitAny/WaitAll is already undefined elsewhere in this API).
    let probeDrainStreams () : Stream option * Stream option =
        lock stateLock (fun () ->
            if consumption = Consumption.Fresh then
                stdoutStream, stderrStream
            else
                None, None)

    let elapsed () =
        Stopwatch.GetElapsedTime host.StartedTimestamp

    // The per-run correlation id: the verb layer stamps one (shared across a run's retries); a direct
    // spawn with none gets a fresh per-incarnation id. Carried on every run-scoped log/trace event.
    let runId =
        match config.RunId with
        | Some id -> id
        | None -> Diag.newRunId ()

    // Count the run as started + in-flight, and capture the ambient `Activity` now (at spawn) so the
    // backdated completion span nests under it. Runs once, at construction (like the spawn log). Defined
    // before the timeout arming below, which carries `runId` into the timeout log. The once-guarded
    // conclude/abandon paths (formerly `conclude`/`markAbandoned` with a hand-rolled `concludedFlag`)
    // now live in the shared `RunTelemetryScope` (T-041) — single-consumption already means one terminal
    // verb runs, but its once-guard makes that bulletproof, so metrics can't double-count and a run
    // never yields two spans. An abandoned run (spawned, never driven to a terminal verb) simply isn't
    // counted as completed.
    let telemetry = RunTelemetryScope.Start(config.Program, runId, host.StartTime)

    let conclude (outcome: Outcome) =
        telemetry.Conclude(config.Logger, outcome, host.Pid, elapsed ())

    // Clear the `runs.active` mark for a run whose handle is being disposed without ever having reached
    // a terminal verb (a streaming/event-driven handle the caller only consumed and dropped) — a no-op
    // once a terminal verb has already run (`telemetry`'s own once-guard).
    let markAbandoned () = telemetry.Abandon()

    // Per-process CPU / peak-memory via the BCL `Process` (reads /proc on Linux, the OS APIs
    // elsewhere) — no metrics once the child has exited or where the platform does not report them.
    let processMetrics (pid: int) : TimeSpan option * int64 option =
        try
            use proc = Process.GetProcessById pid

            let cpu =
                try
                    Some proc.TotalProcessorTime
                with _ ->
                    // Not reported on this platform (e.g. denied / unsupported); omit it.
                    None

            let memory =
                try
                    let peak = proc.PeakWorkingSet64
                    if peak > 0L then Some peak else None
                with _ ->
                    // Peak working set unavailable (some platforms report 0 / throw); omit it.
                    None

            cpu, memory
        with _ ->
            // The process has already exited or is inaccessible — no metrics to read.
            None, None

    // Invoke a per-line callback without allocating a closure per line (which `Option.iter (fun cb ->
    // cb.Invoke line)` would, capturing `line`). On the hot per-line path.
    let invokeLine (callback: Action<string> option) (line: string) =
        match callback with
        | Some cb -> cb.Invoke line
        | None -> ()

    // Reclassify a fault escaping a stdout/stderr pump into a typed `ProcessError.Io` when it is one
    // of the two exception types that only ever reach a pump's caller as a GENUINE read fault —
    // `Pump.readLinesUntilDone`'s `genuineReadFault` already filtered out the routine teardown-race
    // case for the streaming pumps below, and the buffered pumps (`pumpToBuffer` / the discard drains
    // in `WaitAsync`/`ProfileAsync`) never race this handle's own teardown at all (`reapGuard`'s
    // load-bearing invariant: every verb awaits its pumps before disposing). Any other pump fault (a
    // throwing line handler, a decoder failure, an already-typed `ProcessException` from
    // `StreamChannel`'s fail-loud bounded-channel mode) passes through unchanged — T-087.
    let reportedPumpFault (ex: exn) : exn =
        match ex with
        | :? IOException
        | :? ObjectDisposedException -> ProcessException(ProcessError.Io ex.Message) :> exn
        | _ -> ex

    let pumpToBuffer (stream: Stream) encoding terminator tee (callback: Action<string> option) counter =
        task {
            let buffer = Pump.LineBuffer(config.OutputBuffer)

            let onLine (line: string) : ValueTask =
                invokeLine callback line
                counter ()
                buffer.Add line
                ValueTask.CompletedTask

            // Pass the buffer's byte cap as the in-flight line ceiling too, so a newline-free flood
            // can't grow the assembly buffer past it (the forced segments go through `buffer`'s policy).
            //
            // A genuine OS read fault here (`IOException`/`ObjectDisposedException`) is reclassified
            // into `ProcessError.Io` (via `reportedPumpFault`) so the caller reports an honest,
            // incomplete-capture failure instead of a silently truncated success — T-087.
            try
                do!
                    Pump.readLines
                        stream
                        encoding
                        terminator
                        tee
                        onLine
                        config.OutputBuffer.MaxBytes
                        CancellationToken.None
            with
            | :? IOException as ex -> ExceptionDispatchInfo.Throw(reportedPumpFault ex)
            | :? ObjectDisposedException as ex -> ExceptionDispatchInfo.Throw(reportedPumpFault ex)

            return buffer
        }

    // Drain a stream to EOF discarding output (`WaitAsync`/`ProfileAsync`), reclassifying a genuine
    // OS read fault into `ProcessError.Io` exactly like `pumpToBuffer` above — T-087.
    let drainDiscardReporting (stream: Stream option) : Task =
        task {
            try
                do! Pump.drainDiscardOrEmpty stream CancellationToken.None
            with
            | :? IOException as ex -> ExceptionDispatchInfo.Throw(reportedPumpFault ex)
            | :? ObjectDisposedException as ex -> ExceptionDispatchInfo.Throw(reportedPumpFault ex)
        }
        :> Task

    let pumpStdoutBuffer () =
        match stdoutStream with
        | Some s ->
            pumpToBuffer
                s
                config.StdoutEncoding
                config.StdoutLineTerminator
                config.StdoutTee
                config.OnStdoutLine
                bumpStdoutLine
        | None -> Task.FromResult(Pump.LineBuffer config.OutputBuffer)

    let pumpStderrBuffer () =
        match stderrStream with
        | Some s ->
            pumpToBuffer
                s
                config.StderrEncoding
                config.StderrLineTerminator
                config.StderrTee
                config.OnStderrLine
                bumpStderrLine
        | None -> Task.FromResult(Pump.LineBuffer config.OutputBuffer)

    let tooLargeError (totalLines: int) (totalBytes: int) =
        ProcessError.OutputTooLarge(
            config.Program,
            config.OutputBuffer.MaxLines,
            config.OutputBuffer.MaxBytes,
            totalLines,
            totalBytes
        )

    // A genuine stdin-source failure surfaces as `ProcessError.Stdin` only on an otherwise-successful
    // run — an accepted exit code. A non-zero/unaccepted exit or a signal is the "realer" failure and
    // wins: the outcome passes through unchanged so the caller's own classifier sees it. (A cancelled
    // run is already turned into `ProcessError.Cancelled` upstream, before this is reached.)
    let stdinErrorOnSuccess (outcome: Outcome) : ProcessError option =
        if outcome.IsAcceptedBy config.OkCodes then
            host.StdinError()
            |> Option.map (fun ex -> ProcessError.Stdin(config.Program, ex.Message))
        else
            None

    // Wait for exit, applying the configured total and/or idle timeout: on whichever deadline fires,
    // kill the tree (gracefully if `TimeoutGrace` is set, else hard) — one shared kill for both, so no
    // double kill — and report `Outcome.TimedOut`. The idle watchdog is armed inside `raceTimeout` as
    // the wait begins and reset by each stdout/stderr read through the activity-tracking wrappers.
    let waitWithTimeout () : Task<Outcome> =
        let onTimeout (configuredDuration: TimeSpan) : Task =
            task {
                configuredTimeoutDuration <- Some configuredDuration

                match config.TimeoutGrace with
                | Some grace -> do! host.GracefulKill grace
                | None -> host.StartKill()
            }
            :> Task

        Timeouts.raceTimeout config.Logger config.Program runId config.Timeout idleTimer onTimeout (host.Wait())

    // Start (and memoize) a buffered verb's single exit wait, under `stateLock`. Every buffered verb
    // calls this instead of `waitWithTimeout()` directly; the first caller creates the wait, and both
    // the verb that owns the pipes and a concurrent `ExitTask` on the same handle (the "verb, then
    // WaitAny/WaitAll" order) share that one wait — one `host.Wait()`, one set of readers — with
    // correct cross-thread visibility of `bufferedOutcome` in either arrival order.
    let ensureBufferedWait () : Task<Outcome> =
        lock stateLock (fun () ->
            if obj.ReferenceEquals(bufferedOutcome, null) then
                bufferedOutcome <- waitWithTimeout ()

            bufferedOutcome)

    // Kill the tree the moment an output pump faults, so a still-producing child can't wedge the exit
    // wait — and the pump's siblings — by blocking on a full pipe that nobody drains once the pump
    // reading it has died. Fire-and-forget and best-effort: the kill only unblocks the child so the
    // exit wait can conclude and the child is reaped in bounded time even with no configured timeout;
    // the ORIGINAL pump fault is still surfaced by whoever awaits the pump (`Task.WhenAll pumps`
    // below / `streamOutcome` / `eventOutcome`), so what propagates is that fault, not a secondary
    // closed-pipe/channel error. The continuation inspects only `IsFaulted` (never `Exception`), so
    // the pump's exception stays available for its real awaiter, and the continuation itself can't
    // fault (the `StartKill` call is guarded). Runs synchronously on the faulting pump's completion so
    // the kill is prompt.
    let killTreeOnPumpFault (pump: Task) : unit =
        pump.ContinueWith(
            Action<Task>(fun completed ->
                if completed.IsFaulted then
                    try
                        host.StartKill()
                    with _ ->
                        // Best-effort: `reapGuard`'s teardown still reaps the tree, and the pump fault
                        // is surfaced by its awaiter, so a hiccup in this early kill loses nothing.
                        ()),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default
        )
        |> ignore

    // Await a buffered verb's exit wait (`waitTask`, from `ensureBufferedWait`) together with its
    // already-running `pumps`. Fault-aware in both directions:
    //  - A pump fault kills the tree at once (see `killTreeOnPumpFault`), so the child can't wedge
    //    `waitTask` by blocking on a pipe its dead pump no longer drains; `waitTask` then completes
    //    (the killed child is reaped) and the ORIGINAL pump fault surfaces from `Task.WhenAll pumps`.
    //  - `backend.Wait` (the innermost primitive) is designed never to fault, but `waitWithTimeout`
    //    layers a timeout race whose `onTimeout` hook calls native kill syscalls, so the composed wait
    //    CAN throw. `reapGuard`'s teardown disposes the streams the pumps read, so a pump still
    //    in-flight when such a fault escaped this scope would race that dispose; awaiting the pumps
    //    best-effort before re-raising closes that gap.
    // A pump's own fault on the success path (thrown from `Task.WhenAll pumps`) still propagates
    // exactly as before.
    let awaitBufferedOutcome (waitTask: Task<Outcome>) (pumps: Task[]) : Task<Outcome> =
        pumps |> Array.iter killTreeOnPumpFault

        task {
            let mutable error: exn option = None
            let mutable outcome = Unchecked.defaultof<Outcome>

            try
                let! settled = waitTask
                do! Task.WhenAll pumps
                outcome <- settled
            with ex ->
                error <- Some ex
                // A fault from `waitTask` before the pumps were awaited must not orphan them — observe
                // them best-effort. Their own fault, if any, is secondary to the error we surface.
                try
                    do! Task.WhenAll pumps
                with _ ->
                    // best-effort drain; the original fault above is what we report.
                    ()

            match error with
            | Some ex -> return! Task.FromException<Outcome> ex
            | None -> return outcome
        }

    // An async-disposable that reaps the tree on scope exit — normal OR exceptional. Every terminal
    // verb opens one with `use` so the container is always torn down, even when a pump faults (e.g.
    // a throwing line handler) before the verb would otherwise reach its teardown. `Teardown` is
    // idempotent (the group's release runs once), so the redundant call on `RunningProcess` disposal
    // is harmless.
    //
    // Load-bearing invariant: a verb must await ALL of its pumps before this guard's scope exits,
    // because `Teardown` disposes the pipe streams the pumps read — a pump still in-flight at teardown
    // would race a stream `Dispose`. Every verb satisfies this (it awaits the pumps / `streamOutcome`
    // before returning); keep it that way when editing.
    let reapGuard () =
        { new IAsyncDisposable with
            member _.DisposeAsync() =
                // Unblock a writer parked on bounded-stream backpressure before/while tearing down, so
                // it can't outlive this scope (see `disposalCts`'s own comment above).
                disposalCts.Cancel()
                host.Teardown() }

    // Observe any fault on an otherwise fire-and-forget outcome task, so it can never surface as an
    // unobserved task exception at finalization when nothing awaits it (a streaming-only consumer that
    // abandons `FinishAsync`). A consumer that *does* await (`FinishAsync`/`WaitAnyAsync`/`WaitAllAsync`)
    // still re-throws it. Used by both streaming sessions.
    let observeFault (outcomeTask: Task<Outcome>) =
        outcomeTask.ContinueWith(Action<Task<Outcome>>(fun t -> t.Exception |> ignore))
        |> ignore

    // Log the spawn once, at construction. Both this `Log.spawn` and the `RunTelemetryScope.Start`
    // (`Diag.runStarted`) above swallow any fault the consumer's logger / metric / trace sink raises, so
    // constructing this handle can never throw *from observability*. That is what closes the ownership
    // window between the native spawn (already done inside `host`) and the hand-off to the caller: the
    // freshly-spawned tree's deterministic owner — this handle — is always successfully constructed and
    // returned, so a broken logger can never orphan the child here. The runner's construction site
    // (`JobRunner.start`) adds a defence-in-depth teardown as a backstop for any non-observability fault.
    do Log.spawn config.Logger config.Program host.Pid runId

    /// The pid, when known.
    member _.Pid = host.Pid

    /// When the process was started.
    member _.StartTime = host.StartTime

    /// Wall-clock time since the process started.
    member _.Elapsed = elapsed ()

    /// Cumulative CPU time (user + kernel) of the child right now, if the platform reports it and
    /// the process is still alive.
    member _.CpuTime: TimeSpan option =
        match host.Pid with
        | Some pid -> fst (processMetrics pid)
        | None -> None

    /// Peak resident memory of the child in bytes, if reported (some platforms, e.g. macOS, may
    /// not) and the process is still alive.
    member _.PeakMemoryBytes: int64 option =
        match host.Pid with
        | Some pid -> snd (processMetrics pid)
        | None -> None

    /// Total stdout lines pumped so far (counts dropped lines too).
    member _.StdoutLineCount = readStdoutLineCount ()

    /// Total stderr lines pumped so far.
    member _.StderrLineCount = readStderrLineCount ()

    /// Lines dropped so far by a bounded streaming policy's `StreamFullMode.DropOldest`/`DropNewest`
    /// (always `0` unless `Command.StreamBuffer` is configured with one of those modes) — the
    /// streaming analogue of a buffered verb's `ProcessResult.Truncated`.
    member _.DroppedStreamLineCount = Volatile.Read(&droppedStreamLineCount)

    /// Take the interactive stdin handle — `Some` only when the command kept stdin open without a
    /// source attached, and only once.
    member _.TakeStdin() : ProcessStdin option =
        // Under `stateLock` so two concurrent `TakeStdin` calls can't both observe `not stdinTaken`
        // and hand out the same interactive stdin stream twice.
        lock stateLock (fun () ->
            match host.Stdin with
            | Some stream when config.KeepStdinOpen && config.StdinSource.IsNone && not stdinTaken ->
                stdinTaken <- true
                Some(ProcessStdin stream)
            | _ -> None)

    /// Signal the process tree to die without waiting (fire-and-forget, like `Process.Kill()`); the
    /// tree is fully reaped when the handle is disposed. For a blocking kill, dispose the handle.
    member _.Kill() = host.StartKill()

    // The grace window the parameterless `StopAsync()` uses — 2 seconds, matching
    // `ProcessGroupOptions.ShutdownTimeout`'s default so a live handle and its owning group agree on
    // how long a soft stop waits before escalating. Private: it is a documented default, not new
    // public surface.
    static member private DefaultStopGrace = TimeSpan.FromSeconds 2.0

    /// Gracefully stop the process tree, then reap it: send the child a soft signal (SIGTERM), wait
    /// up to `gracePeriod` for it to exit on its own, then hard-kill whatever is still alive — the
    /// same graceful-kill machinery `Command.TimeoutGrace` and `ProcessGroup.ShutdownAsync` drive.
    /// Returns the honest `Outcome` of how the child *actually* concluded (a clean `Exited` if it
    /// obeyed the signal, otherwise a `Signalled`/`Exited` from the escalated kill); a non-zero or
    /// killed exit is data, never a raised error. Unlike the fire-and-forget `Kill()`, this awaits the
    /// stop and tears the tree down before returning, so it is a terminal verb like `WaitAsync`.
    ///
    /// This drains the child's stdout/stderr while it shuts down (a child blocked writing to a full
    /// pipe would otherwise ignore the soft signal until it could flush). If a streaming or capturing
    /// verb already owns the pipes, `StopAsync` reuses that session's wait rather than starting a
    /// second reader on them, so it is safe to call after `StdoutLinesAsync`/`OutputEventsAsync` or
    /// concurrently with an in-flight `FinishAsync`/`WaitAsync`. Idempotent and race-safe with `Kill`,
    /// `Dispose`, and a repeat `StopAsync`: the tree is reaped exactly once.
    ///
    /// **Platform / shared-group degradation (no new silent downgrade).** A soft signal needs a
    /// mechanism that has one. On **Windows** there is no per-tree graceful signal, so `gracePeriod`
    /// is skipped and this is the atomic Job kill — exactly as `Command.TimeoutGrace` and
    /// `ProcessGroup.ShutdownAsync` already degrade there (a console child can still get a best-effort
    /// CTRL+BREAK via `Command.WindowsCtrlSignals()` + `ProcessGroup.Signal`). On a **shared** group
    /// (a handle from `ProcessGroup.StartAsync`, where the group — not the handle — owns the tree)
    /// there is no per-child graceful signal either, so this immediately hard-kills just this child
    /// (like `Kill()`), matching the documented `TimeoutGrace` fallback for a shared group. A handle
    /// from the default runner (`Command.StartAsync()` / `IProcessRunner.SpawnAsync`) owns a private
    /// group and gets the full SIGTERM → grace → SIGKILL on Unix.
    member this.StopAsync(gracePeriod: TimeSpan) : Task<Outcome> =
        task {
            use _reap = reapGuard ()
            // Begin (or reuse) the exit wait BEFORE signalling, so the pipes are drained while the
            // child shuts down. `ExitTask` reuses whichever consumption already owns the pipes (a
            // streaming session, or an in-flight buffered verb) rather than racing a second reader,
            // and claims a fresh buffered drain only when no verb has run yet. It never reaps.
            let exitTask = this.ExitTask
            // Ask the tree to stop: soft signal, wait up to `gracePeriod`, then hard-kill the remainder
            // — reusing `host.GracefulKill`, the timeout machinery's own escalation. Degrades to the
            // documented immediate child/tree kill on Windows or a shared group (see the doc above).
            // Fired at most once (a repeat `StopAsync` only awaits the outcome), so it never re-enters
            // the native kill on a container a prior stop/`Dispose` already released.
            if Interlocked.Exchange(&stopStarted, 1) = 0 then
                do! host.GracefulKill gracePeriod

            let! outcome = exitTask
            // Record the run as completed (once-guarded: a no-op if a concurrent terminal verb sharing
            // the same wait already concluded it). Return the honest outcome; a killed/non-zero exit is
            // data, so this never raises for the stop itself.
            conclude outcome
            return outcome
        }

    /// `StopAsync` using the default 2-second grace window (matching `ProcessGroupOptions.ShutdownTimeout`).
    member this.StopAsync() : Task<Outcome> =
        this.StopAsync RunningProcess.DefaultStopGrace

    /// Run to completion, capturing stdout as decoded text. A non-zero exit is data; the tree is
    /// reaped when the call returns.
    member _.OutputStringAsync() : Task<Result<ProcessResult<string>, ProcessError>> =
        if not (claimBuffered ()) then
            Task.FromResult(Error(alreadyConsumedError ()))
        else

            task {
                use _reap = reapGuard ()
                let stdoutTask = pumpStdoutBuffer ()
                let stderrTask = pumpStderrBuffer ()
                // Observe BOTH buffer pumps before reading either, so a throwing line handler in one
                // never orphans the other as an unobserved task (mirrors the streaming path's WhenAll);
                // `awaitBufferedOutcome` additionally guarantees this even if the exit wait itself faults.
                let! outcome =
                    awaitBufferedOutcome (ensureBufferedWait ()) [| (stdoutTask :> Task); (stderrTask :> Task) |]

                let! outBuf = stdoutTask
                let! errBuf = stderrTask
                conclude outcome

                if outBuf.TooLarge || errBuf.TooLarge then
                    return
                        Error(
                            tooLargeError
                                (outBuf.TotalLines + errBuf.TotalLines)
                                (outBuf.TotalBytes + errBuf.TotalBytes)
                        )
                else
                    match stdinErrorOnSuccess outcome with
                    | Some err -> return Error err
                    | None ->
                        return
                            Ok(
                                ProcessResult<string>(
                                    config.Program,
                                    outBuf.Text,
                                    errBuf.Text,
                                    outcome,
                                    elapsed (),
                                    outBuf.Truncated || errBuf.Truncated,
                                    config.OkCodes,
                                    ?configuredTimeoutDuration = configuredTimeoutDuration
                                )
                            )
            }

    /// Run to completion, capturing stdout as raw bytes (no line splitting) and stderr as text.
    ///
    /// The configured `OutputBuffer` policy's **byte** controls apply to this raw stdout capture:
    /// `MaxBytes = Some cap` enforces the cap per `Overflow` — `Error` returns
    /// `ProcessError.OutputTooLarge` once the cumulative stdout exceeds the cap (the pipe is still
    /// drained), `DropOldest` keeps the last `cap` bytes, `DropNewest` keeps the first `cap` bytes, both
    /// setting `ProcessResult.Truncated` when anything was dropped. `MaxBytes = None` (the default)
    /// keeps the raw capture **unbounded** — there is no byte ceiling to enforce. `MaxLines` never
    /// applies to a raw byte stream (it has no line structure) and is ignored on stdout here; it still
    /// governs the line-pumped **stderr** capture. `Truncated` reflects truncation of stdout OR stderr,
    /// and `OutputTooLarge` fires if either stream trips its fail-loud ceiling.
    ///
    /// This is a deliberate, documented divergence from the Rust `ProcessKit-rs` reference, whose
    /// `output_bytes` bounds raw bytes only by `Timeout`, not by the buffer policy: a caller who set
    /// `MaxBytes`/`FailLoud` to bound memory would still get an unbounded stdout buffer otherwise.
    member _.OutputBytesAsync() : Task<Result<ProcessResult<byte[]>, ProcessError>> =
        if not (claimBuffered ()) then
            Task.FromResult(Error(alreadyConsumedError ()))
        else

            task {
                use _reap = reapGuard ()

                // The raw stdout capture now honours the byte cap + overflow of `config.OutputBuffer`
                // (unbounded when `MaxBytes = None`, exactly as before); `MaxLines` does not apply to a
                // byte stream, so it is ignored here — it still governs the line-pumped stderr below.
                let stdoutTask =
                    Pump.captureRawOrEmpty stdoutStream config.StdoutTee config.OutputBuffer CancellationToken.None

                let stderrTask = pumpStderrBuffer ()
                // Observe both pumps before reading either, so a throwing stderr handler (or a raw-drain
                // I/O fault) can't orphan the other as an unobserved task; `awaitBufferedOutcome`
                // additionally guarantees this even if the exit wait itself faults.
                let! outcome =
                    awaitBufferedOutcome (ensureBufferedWait ()) [| (stdoutTask :> Task); (stderrTask :> Task) |]

                let! stdoutCapture = stdoutTask
                let! errBuf = stderrTask
                conclude outcome

                if stdoutCapture.TooLarge || errBuf.TooLarge then
                    // The raw stdout byte cap contributes no lines (a byte stream has none); stderr is
                    // line-pumped, so its totals carry the lines and both streams' bytes are summed.
                    return Error(tooLargeError errBuf.TotalLines (stdoutCapture.TotalBytes + errBuf.TotalBytes))
                else
                    match stdinErrorOnSuccess outcome with
                    | Some err -> return Error err
                    | None ->
                        return
                            Ok(
                                ProcessResult<byte[]>(
                                    config.Program,
                                    stdoutCapture.Bytes,
                                    errBuf.Text,
                                    outcome,
                                    elapsed (),
                                    stdoutCapture.Truncated || errBuf.Truncated,
                                    config.OkCodes,
                                    ?configuredTimeoutDuration = configuredTimeoutDuration
                                )
                            )
            }

    /// Wait for the process to exit, discarding its output. Reaps the tree.
    member _.WaitAsync() : Task<Outcome> =
        if not (claimBuffered ()) then
            raise (InvalidOperationException alreadyConsumedMessage)

        task {
            use _reap = reapGuard ()
            // Drain both pipes (so the child never blocks on a full buffer) without retaining.
            let stdoutTask = drainDiscardReporting stdoutStream
            let stderrTask = drainDiscardReporting stderrStream
            // Observe both drains together so an I/O fault on one can't orphan the other;
            // `awaitBufferedOutcome` additionally guarantees this even if the exit wait itself faults.
            let! outcome = awaitBufferedOutcome (ensureBufferedWait ()) [| stdoutTask; stderrTask |]
            conclude outcome
            return outcome
        }

    /// Run to completion while periodically sampling the child's CPU/memory every `interval`, and
    /// return a `RunProfile`. Drains and discards output (like `WaitAsync`) and reaps the tree.
    member _.ProfileAsync(interval: TimeSpan) : Task<RunProfile> =
        if not (claimBuffered ()) then
            raise (InvalidOperationException alreadyConsumedMessage)

        task {
            use _reap = reapGuard ()

            let period =
                if interval <= TimeSpan.Zero then
                    TimeSpan.FromMilliseconds 1.0
                else
                    interval

            let mutable samples = 0
            let mutable lastCpu = None
            let mutable peakMemory = None
            use sampleCts = new CancellationTokenSource()

            let sampler =
                task {
                    try
                        while not sampleCts.IsCancellationRequested do
                            match host.Pid with
                            | Some pid ->
                                let cpu, memory = processMetrics pid
                                cpu |> Option.iter (fun c -> lastCpu <- Some c)

                                match memory with
                                | Some m ->
                                    peakMemory <-
                                        Some(
                                            match peakMemory with
                                            | Some existing -> max existing m
                                            | None -> m
                                        )
                                | None -> ()
                            | None -> ()

                            samples <- samples + 1
                            // Clamp so an over-long sampling period can't throw out of `Task.Delay`.
                            do! Task.Delay(Timeouts.clampArmable period, sampleCts.Token)
                    with :? OperationCanceledException ->
                        // The run finished and we cancelled sampling; stop quietly.
                        ()
                }

            let stdoutTask = drainDiscardReporting stdoutStream
            let stderrTask = drainDiscardReporting stderrStream

            // Capture a fault rather than letting it escape immediately, so the sampler is ALWAYS
            // cancelled and awaited before its CTS is disposed at scope exit — never left running as
            // an unobserved task. (A task CE cannot `do!` inside a `finally`, so this is the
            // try/with-then-single-cleanup form of try/finally; the cleanup must also precede reading
            // the sampler's metrics on the success path, which a `finally`/`use` could not guarantee.)
            let mutable error: exn option = None
            let mutable outcome = Unchecked.defaultof<Outcome>

            try
                let! settled = ensureBufferedWait ()
                do! Task.WhenAll([| stdoutTask; stderrTask |])
                outcome <- settled
            with ex ->
                error <- Some ex
                // A fault before the drains were awaited (e.g. waitWithTimeout threw) must not orphan
                // them — observe them best-effort. Their own fault is secondary to the error we surface.
                try
                    do! Task.WhenAll([| stdoutTask; stderrTask |])
                with _ ->
                    // best-effort teardown drain; the original fault above is what we report.
                    ()

            sampleCts.Cancel()
            do! sampler

            match error with
            | Some ex -> return! Task.FromException<RunProfile> ex
            | None ->
                conclude outcome
                return RunProfile(outcome, elapsed (), lastCpu, peakMemory, samples)
        }

    /// `ProfileAsync` sampling every 100 ms.
    member this.ProfileAsync() =
        this.ProfileAsync(TimeSpan.FromMilliseconds 100.0)

    // Returns false when a different consumption (a buffered verb, or event streaming) already owns the
    // pipes; true once the stdout streaming session is (or already was) ours. The whole check + claim +
    // session setup runs under `stateLock`, so a concurrent second `StdoutLinesAsync`/`WaitForLineAsync`/
    // `FinishAsync` either observes a fully-constructed session (channel + pumps + `streamOutcome` all
    // assigned) or, if it is an incompatible consumer, is atomically refused — never a half-built
    // session, and never two racing setups building two readers on the one channel.
    member private _.StartStdoutStreaming() : bool =
        lock stateLock (fun () ->
            if consumption = Consumption.StdoutStreaming then
                true
            elif consumption <> Consumption.Fresh then
                false
            else
                consumption <- Consumption.StdoutStreaming
                let stderrBuffer = Pump.LineBuffer(config.OutputBuffer)
                stderrStreamBuffer <- stderrBuffer

                let stdoutPump =
                    task {
                        try
                            do!
                                StreamChannel.pumpLines
                                    stdoutStream
                                    config.StdoutEncoding
                                    config.StdoutLineTerminator
                                    config.StdoutTee
                                    (fun line ->
                                        invokeLine config.OnStdoutLine line
                                        bumpStdoutLine ()

                                        writeStreamItem
                                            stdoutChannel.Writer
                                            stdoutChannel.Reader
                                            readStdoutLineCount
                                            bumpDroppedStreamLine
                                            line)
                                    (fun () -> disposalCts.Token.IsCancellationRequested)

                            stdoutChannel.Writer.Complete()
                        with ex ->
                            // A pump fault — a throwing `OnStdoutLine` handler, `StreamFullMode.Error`
                            // tripping its cap, or a genuine OS read fault (reclassified into
                            // `ProcessError.Io` by `reportedPumpFault` — T-087) — must still complete the
                            // channel, carrying the error, so a `StdoutLinesAsync` consumer observes it
                            // instead of hanging on a reader that never ends. Re-raise (preserving the
                            // original stack; `reraise` is unavailable inside a task CE) so `streamOutcome`
                            // / `FinishAsync` surface the same fault.
                            let reported = reportedPumpFault ex
                            stdoutChannel.Writer.Complete reported
                            ExceptionDispatchInfo.Throw reported
                    }

                let stderrPump =
                    task {
                        try
                            do!
                                StreamChannel.pumpLines
                                    stderrStream
                                    config.StderrEncoding
                                    config.StderrLineTerminator
                                    config.StderrTee
                                    (fun line ->
                                        invokeLine config.OnStderrLine line
                                        bumpStderrLine ()
                                        stderrBuffer.Add line
                                        ValueTask.CompletedTask)
                                    (fun () -> disposalCts.Token.IsCancellationRequested)
                        with ex ->
                            // A genuine OS read fault is reclassified into `ProcessError.Io` (T-087) before
                            // it faults `streamOutcome` / `FinishAsync` below.
                            ExceptionDispatchInfo.Throw(reportedPumpFault ex)
                    }

                // A fault in either pump kills the tree at once, so a still-producing child can't wedge
                // `waitWithTimeout()` (below) by blocking on a pipe its dead pump no longer drains — the
                // exit wait then completes and `streamOutcome` surfaces the original pump fault.
                killTreeOnPumpFault (stdoutPump :> Task)
                killTreeOnPumpFault (stderrPump :> Task)

                streamOutcome <-
                    task {
                        let! outcome = waitWithTimeout ()
                        // Await both pumps together so neither task is left unobserved if the other faults.
                        do! Task.WhenAll([| stdoutPump :> Task; stderrPump :> Task |])
                        return outcome
                    }

                // A `StdoutLinesAsync()` consumer can abandon `FinishAsync()` (e.g. its enumeration throws
                // because a faulting `OnStdoutLine` handler completed the channel with the error), so observe
                // the outcome fault here.
                observeFault streamOutcome

                true)

    /// Stream stdout line by line as it arrives. Call `FinishAsync` afterwards for stderr + outcome.
    member this.StdoutLinesAsync() : IAsyncEnumerable<string> =
        if not (this.StartStdoutStreaming()) then
            raise (InvalidOperationException alreadyConsumedMessage)

        stdoutChannel.Reader.ReadAllAsync()

    /// After streaming stdout, wait for exit and return the captured stderr. Reaps the tree.
    member this.FinishAsync() : Task<Result<Finished, ProcessError>> =
        if not (this.StartStdoutStreaming()) then
            Task.FromResult(Error(alreadyConsumedError ()))
        else

            task {
                use _reap = reapGuard ()
                let! outcome = streamOutcome
                conclude outcome

                if stderrStreamBuffer.TooLarge then
                    return Error(tooLargeError stderrStreamBuffer.TotalLines stderrStreamBuffer.TotalBytes)
                else
                    match stdinErrorOnSuccess outcome with
                    | Some err -> return Error err
                    | None -> return Ok(Finished(outcome, stderrStreamBuffer.Text))
            }

    // Returns false when a different consumption (a buffered verb, or stdout streaming) already owns the
    // pipes; true once the event streaming session is (or already was) ours. As with
    // `StartStdoutStreaming`, the whole check + claim + setup runs under `stateLock`, so a concurrent
    // second `OutputEventsAsync` observes a fully-constructed session or is atomically refused.
    member private _.StartEventStreaming() : bool =
        lock stateLock (fun () ->
            if consumption = Consumption.EventStreaming then
                true
            elif consumption <> Consumption.Fresh then
                false
            else
                consumption <- Consumption.EventStreaming
                // Each pump completes the shared event channel on its own fault (carrying the error), so an
                // `OutputEventsAsync` consumer observes a throwing handler promptly rather than hanging until the
                // process exits — `eventOutcome` below only completes the channel after the exit wait, which
                // for a long-running child can be far away. `TryComplete` because the two pumps and the
                // combined task below all race to complete the one channel; re-raise so `eventOutcome` faults.
                // One helper for both streams so the fault-completion invariant lives in a single place.
                let eventPump
                    (stream: Stream option)
                    encoding
                    terminator
                    tee
                    (onLine: Action<string> option)
                    (bump: unit -> unit)
                    (countSoFar: unit -> int)
                    (wrap: OutputLine -> OutputEvent)
                    =
                    task {
                        try
                            do!
                                StreamChannel.pumpLines
                                    stream
                                    encoding
                                    terminator
                                    tee
                                    (fun line ->
                                        invokeLine onLine line
                                        bump ()

                                        writeStreamItem
                                            eventChannel.Writer
                                            eventChannel.Reader
                                            countSoFar
                                            bumpDroppedStreamLine
                                            (wrap (OutputLine line)))
                                    (fun () -> disposalCts.Token.IsCancellationRequested)
                        with ex ->
                            // A genuine OS read fault is reclassified into `ProcessError.Io` (T-087)
                            // before it completes the channel / faults `eventOutcome` below.
                            let reported = reportedPumpFault ex
                            eventChannel.Writer.TryComplete reported |> ignore
                            ExceptionDispatchInfo.Throw reported
                    }

                let stdoutPump =
                    eventPump
                        stdoutStream
                        config.StdoutEncoding
                        config.StdoutLineTerminator
                        config.StdoutTee
                        config.OnStdoutLine
                        bumpStdoutLine
                        readStdoutLineCount
                        OutputEvent.Stdout

                let stderrPump =
                    eventPump
                        stderrStream
                        config.StderrEncoding
                        config.StderrLineTerminator
                        config.StderrTee
                        config.OnStderrLine
                        bumpStderrLine
                        readStderrLineCount
                        OutputEvent.Stderr

                // A fault in either pump kills the tree at once, so a still-producing child can't wedge
                // `waitWithTimeout()` (below) by blocking on a pipe its dead pump no longer drains — the
                // exit wait then completes and `eventOutcome` surfaces the original pump fault.
                killTreeOnPumpFault (stdoutPump :> Task)
                killTreeOnPumpFault (stderrPump :> Task)

                eventOutcome <-
                    task {
                        let mutable error: exn option = None
                        let mutable outcome = Unchecked.defaultof<Outcome>

                        try
                            let! settled = waitWithTimeout ()
                            outcome <- settled
                            // Await both pumps together so neither is left unobserved if the other faults.
                            do! Task.WhenAll([| stdoutPump :> Task; stderrPump :> Task |])
                            eventChannel.Writer.TryComplete() |> ignore
                        with ex ->
                            error <- Some ex
                            // A fault (a throwing handler, or the exit wait itself) completes the channel WITH
                            // the error so an `OutputEventsAsync` consumer observes it instead of hanging — idempotent
                            // with the per-pump completion above. The fault is otherwise consumed here (and by
                            // the ContinueWith below) rather than surfacing as an unobserved task exception.
                            eventChannel.Writer.TryComplete ex |> ignore

                        // Surface the outcome, or re-raise the fault for a concurrent ExitTask (WaitAny/WaitAll
                        // on this handle). The ContinueWith below observes that fault, so the OutputEvents-only
                        // case never leaves an unobserved task exception.
                        match error with
                        | Some ex -> return! Task.FromException<Outcome> ex
                        | None ->
                            conclude outcome
                            return outcome
                    }

                // Observe any fault on this otherwise fire-and-forget task (the OutputEvents-only case, where
                // nothing awaits `ExitTask`).
                observeFault eventOutcome

                true)

    /// Stream merged stdout+stderr line events as they arrive, each tagged with its origin
    /// (`OutputEvent.Stdout`/`OutputEvent.Stderr`). Under `Command.MergeStderr` the child has no separate
    /// stderr stream (it is folded into stdout at the OS level), so every event is an `OutputEvent.Stdout`
    /// — the stderr lines are already interleaved, in order, within the stdout byte stream.
    member this.OutputEventsAsync() : IAsyncEnumerable<OutputEvent> =
        if not (this.StartEventStreaming()) then
            raise (InvalidOperationException alreadyConsumedMessage)

        eventChannel.Reader.ReadAllAsync()

    /// Wait until a stdout line satisfies `predicate`, or fail with `NotReady` after `timeout`
    /// (or `Cancelled` if `cancellationToken` fires first). Consumed lines are not re-delivered; a
    /// later `StdoutLinesAsync`/`FinishAsync` sees the rest.
    member this.WaitForLineAsync
        (predicate: Func<string, bool>, timeout: TimeSpan, [<Optional>] cancellationToken: CancellationToken)
        : Task<Result<string, ProcessError>> =
        ArgumentNullException.ThrowIfNull predicate

        if not (this.StartStdoutStreaming()) then
            Task.FromResult(Error(alreadyConsumedError ()))
        else

            task {
                // Clamp so an out-of-range timeout can't throw out of the CTS constructor (a negative
                // value fires immediately → NotReady); the reported NotReady still carries the original.
                use timeoutCts = new CancellationTokenSource(Timeouts.clampArmable timeout)

                use linked =
                    CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, cancellationToken)

                try
                    let mutable matched = false
                    let mutable found = Unchecked.defaultof<string>

                    while not matched do
                        let! line = stdoutChannel.Reader.ReadAsync linked.Token

                        if predicate.Invoke line then
                            found <- line
                            matched <- true

                    return Ok found
                with
                | :? OperationCanceledException ->
                    // The caller's token wins over the deadline: a cancelled wait is an error, a
                    // timed-out one is "not ready yet".
                    if cancellationToken.IsCancellationRequested then
                        return Error(ProcessError.Cancelled config.Program)
                    else
                        return Error(ProcessError.NotReady(config.Program, timeout))
                | :? ChannelClosedException as ex ->
                    // The stdout pump completed the channel. A clean EOF (stdout ended before a matching
                    // line) means the readiness condition was never met → NotReady. But a pump FAULT (a
                    // throwing `OnStdoutLine`/`StdoutTee` handler, or a decode/IO error) completed it WITH
                    // that exception as the InnerException; re-raise it (preserving its stack) so a real
                    // bug surfaces exactly as it does through `FinishAsync`/`StdoutLinesAsync`, rather than
                    // being masked as a spurious readiness timeout that also returns before the deadline.
                    match ex.InnerException with
                    | null -> return Error(ProcessError.NotReady(config.Program, timeout))
                    | inner ->
                        ExceptionDispatchInfo.Throw inner
                        return Unchecked.defaultof<_>
            }

    /// Wait until a TCP connection to `endpoint` succeeds, or fail with `NotReady` after `timeout`
    /// (or `Cancelled` if `cancellationToken` fires first). Background-drains (and discards) the
    /// child's piped stdout/stderr for the duration of the poll — like `WaitForLineAsync`, so a child
    /// that writes more than one OS pipe buffer of startup output (~64 KiB on Linux) before becoming
    /// ready can't block in `write()` and spuriously time out this probe — but unlike
    /// `WaitForLineAsync`, the drained bytes are discarded rather than handed back, and draining stops
    /// once the probe concludes rather than continuing as an established streaming session. A capture
    /// verb (`OutputStringAsync`/`OutputBytesAsync`/`StdoutLinesAsync`/`OutputEventsAsync`) called
    /// AFTER this probe therefore only sees what the child wrote after the probe concluded, not the
    /// full run — the same "doesn't compose with a subsequent fresh capture" limitation
    /// `WaitForLineAsync` already documents, now uniform across all three readiness probes. If a
    /// buffered/streaming verb already claimed the pipes before this call, that verb's own pump is
    /// already draining them and this probe leaves them alone (no second reader).
    member _.WaitForPortAsync
        (endpoint: IPEndPoint, timeout: TimeSpan, [<Optional>] cancellationToken: CancellationToken)
        : Task<Result<unit, ProcessError>> =
        ArgumentNullException.ThrowIfNull endpoint
        let stdout, stderr = probeDrainStreams ()
        ReadinessProbe.waitForPort config.Program stdout stderr endpoint timeout cancellationToken

    /// Poll `probe` until it returns true, or fail with `NotReady` after `timeout` (or `Cancelled`
    /// if `cancellationToken` fires first). Background-drains (and discards) the child's piped
    /// stdout/stderr for the duration of the poll, exactly like `WaitForPortAsync` — see its doc for
    /// what that does and doesn't compose with afterward.
    member _.WaitForAsync
        (probe: Func<Task<bool>>, timeout: TimeSpan, [<Optional>] cancellationToken: CancellationToken)
        : Task<Result<unit, ProcessError>> =
        ArgumentNullException.ThrowIfNull probe
        let stdout, stderr = probeDrainStreams ()
        ReadinessProbe.waitFor config.Program stdout stderr probe timeout cancellationToken

    /// A memoized task that waits for the process to exit (draining its pipes) without reaping it —
    /// the racing primitive behind `WaitAnyAsync`/`WaitAllAsync`. Built exactly once under `stateLock`
    /// (so concurrent `WaitAnyAsync`/`WaitAllAsync` on the same handle can't create two racing waits),
    /// reusing whichever consumption already owns the pipes instead of ever starting a second reader:
    /// - `StdoutStreaming`/`EventStreaming`: the session's own combined outcome.
    /// - `Buffered` (a capture verb already started — the "verb, then WaitAny/WaitAll" order): the
    ///   verb's own single wait, shared via `ensureBufferedWait` (memoized under the same lock, so it
    ///   is observed here regardless of which of the two reached it first).
    /// - `Fresh` (WaitAny/WaitAll arrives first): claims the buffered slot itself and runs its own
    ///   drains, so a terminal verb called afterwards on the same handle is refused
    ///   (`alreadyConsumedError`) rather than racing a second reader.
    member internal _.ExitTask: Task<Outcome> =
        lock stateLock (fun () ->
            if not exitStarted then
                exitStarted <- true

                exitTaskValue <-
                    if consumption = Consumption.StdoutStreaming then
                        streamOutcome
                    elif consumption = Consumption.EventStreaming then
                        // The event pumps already drain both pipes; reuse their shared outcome rather than
                        // starting our own drains here, which would race a second reader on the same streams.
                        eventOutcome
                    elif consumption = Consumption.Buffered then
                        // A buffered verb already claimed the pipes; share its single wait (memoized under
                        // this same lock) rather than starting a second pair of readers and a second
                        // `host.Wait()`. Its own pumps drain the pipes, so the reused wait needs none.
                        ensureBufferedWait ()
                    else
                        // Fresh: no verb has run yet. Claim the buffered slot (inline — we already hold
                        // `stateLock`) so a terminal verb called after WaitAny/WaitAll on the same handle
                        // is refused rather than racing a second reader on these pipes.
                        consumption <- Consumption.Buffered

                        task {
                            // These drains are fire-and-forget for a race loser the caller may dispose
                            // mid-drain, so they must complete quietly on teardown rather than fault unobserved.
                            let stdoutDrain =
                                Pump.drainDiscardOrEmptyUntilDone stdoutStream CancellationToken.None

                            let stderrDrain =
                                Pump.drainDiscardOrEmptyUntilDone stderrStream CancellationToken.None

                            let! outcome = waitWithTimeout ()
                            do! Task.WhenAll([| stdoutDrain; stderrDrain |])
                            // Racing this handle to exit *is* its completion (conclude does not reap, so the
                            // no-reap contract holds), so a `WaitAny`/`WaitAll`-only run still records its
                            // exit/metrics/span and clears the in-flight mark. Once-guarded, so a terminal verb
                            // afterwards (already refused by the buffered claim above) can't double-count.
                            conclude outcome
                            return outcome
                        }

            exitTaskValue)

    /// Wait for the first of `processes` to exit; returns its index and outcome. Does not reap any
    /// of them — dispose them yourself. Safe to call on a handle a buffered verb (`OutputStringAsync`/
    /// `OutputBytesAsync`/`WaitAsync`/`ProfileAsync`) already started: it reuses that verb's own wait
    /// (see `ExitTask`) rather than racing a second reader on the same pipes.
    ///
    /// `processes` must be non-null, non-empty, and free of null elements — each is a programmer
    /// error, not a process outcome, so it throws (`ArgumentNullException` for a null array,
    /// `ArgumentException` for an empty array or a null element) rather than reporting through a
    /// `Result`. Symmetric with `WaitAllAsync` on all three axes: error channel, empty input, and
    /// null handling. If a pump backing one of the raced `ExitTask`s faults, that exception propagates
    /// unchanged from the awaited task — also not wrapped in a `Result`.
    static member WaitAnyAsync(processes: RunningProcess[]) : Task<WaitAnyResult> =
        ArgumentNullException.ThrowIfNull processes

        if processes.Length = 0 then
            raise (ArgumentException("expected at least one process", nameof processes))

        if processes |> Array.exists (fun p -> obj.ReferenceEquals(p, null)) then
            raise (ArgumentException("processes must not contain a null element", nameof processes))

        task {
            let tasks = processes |> Array.map (fun p -> p.ExitTask)
            let! completed = Task.WhenAny tasks
            let index = tasks |> Array.findIndex (fun t -> obj.ReferenceEquals(t, completed))
            let! outcome = completed
            return WaitAnyResult(index, outcome)
        }

    /// Wait for all of `processes` to exit; returns their outcomes in order. Does not reap them.
    ///
    /// `processes` must be non-null, non-empty, and free of null elements — each is a programmer
    /// error, not a process outcome, so it throws (`ArgumentNullException` for a null array,
    /// `ArgumentException` for an empty array or a null element) rather than reporting through a
    /// `Result`. Symmetric with `WaitAnyAsync` on all three axes: error channel, empty input, and null
    /// handling. If a pump backing one of the `ExitTask`s faults, that exception propagates unchanged
    /// from `Task.WhenAll` — also not wrapped in a `Result`.
    static member WaitAllAsync(processes: RunningProcess[]) : Task<Outcome[]> =
        ArgumentNullException.ThrowIfNull processes

        if processes.Length = 0 then
            raise (ArgumentException("expected at least one process", nameof processes))

        if processes |> Array.exists (fun p -> obj.ReferenceEquals(p, null)) then
            raise (ArgumentException("processes must not contain a null element", nameof processes))

        processes |> Array.map (fun p -> p.ExitTask) |> Task.WhenAll

    interface IAsyncDisposable with
        member _.DisposeAsync() =
            disposalCts.Cancel()
            // Stop and release the idle-timeout watchdog (if any); a pump still resetting it races this
            // harmlessly (`Reset` after disposal is a no-op).
            idleTimer |> Option.iter (fun t -> (t :> IDisposable).Dispose())
            // Clear `runs.active` for a handle disposed without ever reaching a terminal verb — a no-op
            // (guarded by `concludedFlag`) when `conclude` already ran, so a normal verb-then-dispose
            // sequence, or a repeated dispose, cannot double-decrement.
            markAbandoned ()
            host.Teardown()
