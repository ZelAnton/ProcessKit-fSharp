namespace ProcessKit

open System
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.Net
open System.Net.Sockets
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
    let mutable stdinTaken = false
    let mutable stdoutLineCount = 0
    let mutable stderrLineCount = 0
    let mutable droppedStreamLineCount = 0
    let mutable stderrStreamBuffer = Unchecked.defaultof<Pump.LineBuffer>
    let mutable streamOutcome = Unchecked.defaultof<Task<Outcome>>

    let bumpDroppedStreamLine () =
        Interlocked.Increment(&droppedStreamLineCount) |> ignore

    // Cancels a writer parked on a bounded stream's `StreamFullMode.Backpressure` (`WriteAsync`) once
    // this handle is torn down, so an abandoned bounded stream can't leave its pump running forever: a
    // `Command.Timeout` kills the CHILD but does not by itself free a writer waiting here if nothing
    // ever reads again (see the deadlock note in docs/streaming.md). No `CancelAfter` is ever armed on
    // it, so it owns no timer — there is nothing to release, and skipping `Dispose` is safe.
    let disposalCts = new CancellationTokenSource()

    // A bounded channel for an opt-in `StreamBufferPolicy`. `SingleReader = false` regardless of
    // `FullMode` (not just for `DropOldest`, which needs the writer to evict via `Reader.TryRead`) —
    // one uniform construction path is simpler than a mode-dependent one, and the cost only applies to
    // an opt-in bounded stream, never to the default. Every full mode is otherwise implemented over
    // `BoundedChannelFullMode.Wait`'s precise, non-blocking "is it full?" signal (`TryWrite`'s bool) —
    // the channel's own built-in Drop full-modes always report `TryWrite` success, which would hide
    // whether a drop actually happened.
    let boundedOptions (capacity: int) (singleWriter: bool) =
        BoundedChannelOptions(
            capacity,
            SingleReader = false,
            SingleWriter = singleWriter,
            FullMode = BoundedChannelFullMode.Wait
        )

    // Single-reader/single-writer *unbounded* channels remain the unconditional default: each is
    // consumed by exactly one reader, and the stdout channel is written by exactly one pump (the event
    // channel by two), selecting the faster single-consumer channel implementation. Opting in to
    // `Command.StreamBuffer` switches both to the bounded construction above instead.
    let stdoutChannel: Channel<string> =
        match config.StreamBuffer with
        | Some policy -> Channel.CreateBounded<string>(boundedOptions policy.Capacity true)
        | None -> Channel.CreateUnbounded<string>(UnboundedChannelOptions(SingleReader = true, SingleWriter = true))

    let eventChannel: Channel<OutputEvent> =
        match config.StreamBuffer with
        | Some policy -> Channel.CreateBounded<OutputEvent>(boundedOptions policy.Capacity false)
        | None ->
            Channel.CreateUnbounded<OutputEvent>(UnboundedChannelOptions(SingleReader = true, SingleWriter = false))

    // Write one item to a (possibly bounded) channel per `config.StreamBuffer` (`None` = today's
    // unbounded `TryWrite`, unchanged). `Backpressure` awaits room via `WriteAsync`, bounded to
    // `disposalCts.Token` so an abandoned bounded stream's writer can't outlive this handle.
    // `DropNewest`/`DropOldest` keep the channel's item count bounded losslessly but the CONTENT is
    // lossy, bumping `onDrop`. `Error` faults the pump with `ProcessError.OutputTooLarge` once full —
    // reusing the exact fault path a throwing per-line handler already goes through (the caller's
    // `try`/`with` completes the channel and re-raises).
    let writeStreamItem
        (writer: ChannelWriter<'T>)
        (reader: ChannelReader<'T>)
        (countSoFar: unit -> int)
        (onDrop: unit -> unit)
        (item: 'T)
        : ValueTask =
        match config.StreamBuffer with
        | None ->
            writer.TryWrite item |> ignore
            ValueTask.CompletedTask
        | Some policy ->
            match policy.FullMode with
            | StreamFullMode.Backpressure -> writer.WriteAsync(item, disposalCts.Token)
            | StreamFullMode.DropNewest ->
                if not (writer.TryWrite item) then
                    onDrop ()

                ValueTask.CompletedTask
            | StreamFullMode.DropOldest ->
                // Full: evict the oldest queued item ourselves — safe because bounded channels are
                // always created with SingleReader = false — then retry, looping rather than retrying
                // once: the event channel has two concurrent writers (stdout + stderr), so a sibling
                // pump can refill the freed slot before our retry lands. Looping keeps `onDrop` exactly
                // in step with actual evictions instead of under-counting on that race (a single-writer
                // stdout-only stream always succeeds on the first iteration).
                //
                // Bounded to genuine progress: if a sibling pump has completed the channel (its own
                // fault path — a throwing handler, a decode/IO error — calls `Writer.TryComplete ex`),
                // both `TryRead` and `TryWrite` permanently return `false`; without this check the loop
                // would spin forever (a livelock pinning a CPU core, and `eventOutcome`/`FinishAsync`
                // would never complete). Capacity is always >= 1 (`StreamBufferPolicy.Bounded` rejects
                // less), so a non-completed channel reporting `TryWrite` full always has something to
                // evict — `TryRead` failing here is therefore only possible once the channel is done.
                let mutable written = writer.TryWrite item
                let mutable canRetry = true

                while not written && canRetry do
                    let evicted, _ = reader.TryRead()

                    if evicted then
                        onDrop ()
                        written <- writer.TryWrite item
                    else
                        // Nothing left to evict and nowhere to write: the channel is done. This item
                        // can't be delivered either way — count it dropped and stop instead of spinning.
                        onDrop ()
                        canRetry <- false

                ValueTask.CompletedTask
            | StreamFullMode.Error ->
                if writer.TryWrite item then
                    ValueTask.CompletedTask
                else
                    raise (
                        ProcessException(
                            ProcessError.OutputTooLarge(config.Program, Some policy.Capacity, None, countSoFar (), 0)
                        )
                    )

    let mutable exitStarted = false
    let mutable exitTaskValue = Unchecked.defaultof<Task<Outcome>>

    // The event-streaming session's single combined outcome (waiting for exit + draining both pipes via
    // the two pumps). ExitTask reuses it for an `EventStreaming` handle so it does not start a second,
    // racing set of drains on the same streams.
    let mutable eventOutcome = Unchecked.defaultof<Task<Outcome>>

    // Single-consumption guard: the output pipes are pumped exactly once. A buffered one-shot verb
    // (OutputString/OutputBytes/Wait/Profile) consumes them whole; the streaming verbs form one
    // session (`StdoutStreaming`: StdoutLines/WaitForLine/Finish share the stdout channel;
    // `EventStreaming`: OutputEvents owns the event channel). A second, different consumer would race
    // two readers on the same pipe — splitting/losing output — so it is refused. Verb calls are
    // assumed sequential (the class is not built for concurrent verbs), so a plain field suffices.
    let mutable consumption = Consumption.Fresh

    // Claim the pipes for a one-shot buffered verb — only from fresh (no re-entry: a second buffered
    // verb would re-pump already-torn-down streams).
    let claimBuffered () =
        if consumption = Consumption.Fresh then
            consumption <- Consumption.Buffered
            true
        else
            false

    // Claim the pipes for a streaming session, allowing re-entry of the *same* streaming mode (so
    // StdoutLines → Finish, or repeated StdoutLines, compose) but rejecting a different consumer.
    let claimStreaming (mode: Consumption) =
        if consumption = Consumption.Fresh then
            consumption <- mode
            true
        else
            consumption = mode

    let alreadyConsumedMessage =
        "this RunningProcess has already been consumed by another verb"

    let alreadyConsumedError () =
        ProcessError.Unsupported alreadyConsumedMessage

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
    // before the timeout arming below, which carries `runId` into the timeout log.
    let spawnParentContext = Diag.runStarted config.Program

    // The single exit hook: log + metrics + (backdated) trace span, at each terminal verb's exit point.
    // Single-consumption already means one terminal verb runs, but the once-guard makes it bulletproof —
    // conclude fires at most once per run, so metrics can't double-count and a run never yields two spans.
    // An abandoned run (spawned, never driven to a terminal verb) simply isn't counted as completed.
    let concludedFlag = ref 0

    let conclude (outcome: Outcome) =
        if Interlocked.Exchange(&concludedFlag.contents, 1) = 0 then
            let duration = elapsed ()
            Log.exit config.Logger config.Program outcome duration runId
            Diag.runCompleted config.Program runId outcome host.Pid host.StartTime duration spawnParentContext

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

    let pumpToBuffer (stream: Stream) encoding tee (callback: Action<string> option) counter =
        task {
            let buffer = Pump.LineBuffer(config.OutputBuffer)

            let onLine (line: string) : ValueTask =
                invokeLine callback line
                counter ()
                buffer.Add line
                ValueTask.CompletedTask

            // Pass the buffer's byte cap as the in-flight line ceiling too, so a newline-free flood
            // can't grow the assembly buffer past it (the forced segments go through `buffer`'s policy).
            do! Pump.readLines stream encoding tee onLine config.OutputBuffer.MaxBytes CancellationToken.None
            return buffer
        }

    let pumpStdoutBuffer () =
        match host.Stdout with
        | Some s ->
            pumpToBuffer s config.StdoutEncoding config.StdoutTee config.OnStdoutLine (fun () ->
                stdoutLineCount <- stdoutLineCount + 1)
        | None -> Task.FromResult(Pump.LineBuffer config.OutputBuffer)

    let pumpStderrBuffer () =
        match host.Stderr with
        | Some s ->
            pumpToBuffer s config.StderrEncoding config.StderrTee config.OnStderrLine (fun () ->
                stderrLineCount <- stderrLineCount + 1)
        | None -> Task.FromResult(Pump.LineBuffer config.OutputBuffer)

    // Pump one stream's lines through `onLine` until the stream ends — the streaming-verb analogue
    // of `pumpToBuffer` (which captures to a `LineBuffer` instead). No-op when the stream isn't
    // piped. The caller owns the sink (a channel writer, a buffer) and any completion signal.
    let pumpLines (stream: Stream option) encoding tee (onLine: string -> ValueTask) =
        task {
            match stream with
            // No in-flight line cap for streaming: it is consumer-paced and applies no buffer policy, so
            // a consumer receives whole lines (the in-flight cap is a buffered-verb concern).
            | Some s -> do! Pump.readLinesUntilDone s encoding tee onLine None CancellationToken.None
            | None -> ()
        }

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

    // Wait for exit, applying a configured timeout: on the deadline, kill the tree (gracefully if
    // `TimeoutGrace` is set, else hard) and report `Outcome.TimedOut`.
    let waitWithTimeout () : Task<Outcome> =
        let onTimeout () : Task =
            task {
                match config.TimeoutGrace with
                | Some grace -> do! host.GracefulKill grace
                | None -> host.StartKill()
            }
            :> Task

        Timeouts.raceTimeout config.Logger config.Program runId config.Timeout onTimeout (host.Wait())

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

    // Log the spawn once, at construction.
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
    member _.StdoutLineCount = stdoutLineCount

    /// Total stderr lines pumped so far.
    member _.StderrLineCount = stderrLineCount

    /// Lines dropped so far by a bounded streaming policy's `StreamFullMode.DropOldest`/`DropNewest`
    /// (always `0` unless `Command.StreamBuffer` is configured with one of those modes) — the
    /// streaming analogue of a buffered verb's `ProcessResult.Truncated`.
    member _.DroppedStreamLineCount = droppedStreamLineCount

    /// Take the interactive stdin handle — `Some` only when the command kept stdin open without a
    /// source attached, and only once.
    member _.TakeStdin() : ProcessStdin option =
        match host.Stdin with
        | Some stream when config.KeepStdinOpen && config.StdinSource.IsNone && not stdinTaken ->
            stdinTaken <- true
            Some(ProcessStdin stream)
        | _ -> None

    /// Signal the process tree to die without waiting (fire-and-forget, like `Process.Kill()`); the
    /// tree is fully reaped when the handle is disposed. For a blocking kill, dispose the handle.
    member _.Kill() = host.StartKill()

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
                let! outcome = waitWithTimeout ()
                // Observe BOTH buffer pumps before reading either, so a throwing line handler in one
                // never orphans the other as an unobserved task (mirrors the streaming path's WhenAll).
                do! Task.WhenAll([| (stdoutTask :> Task); (stderrTask :> Task) |])
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
                                    config.OkCodes
                                )
                            )
            }

    /// Run to completion, capturing stdout as raw bytes (no line splitting) and stderr as text.
    member _.OutputBytesAsync() : Task<Result<ProcessResult<byte[]>, ProcessError>> =
        if not (claimBuffered ()) then
            Task.FromResult(Error(alreadyConsumedError ()))
        else

            task {
                use _reap = reapGuard ()

                let stdoutTask =
                    Pump.drainRawOrEmpty host.Stdout config.StdoutTee CancellationToken.None

                let stderrTask = pumpStderrBuffer ()
                let! outcome = waitWithTimeout ()
                // Observe both pumps before reading either, so a throwing stderr handler (or a raw-drain
                // I/O fault) can't orphan the other as an unobserved task.
                do! Task.WhenAll([| (stdoutTask :> Task); (stderrTask :> Task) |])
                let! stdoutBytes = stdoutTask
                let! errBuf = stderrTask
                conclude outcome

                if errBuf.TooLarge then
                    return Error(tooLargeError errBuf.TotalLines errBuf.TotalBytes)
                else
                    match stdinErrorOnSuccess outcome with
                    | Some err -> return Error err
                    | None ->
                        return
                            Ok(
                                ProcessResult<byte[]>(
                                    config.Program,
                                    stdoutBytes,
                                    errBuf.Text,
                                    outcome,
                                    elapsed (),
                                    errBuf.Truncated,
                                    config.OkCodes
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
            let stdoutTask = Pump.drainDiscardOrEmpty host.Stdout CancellationToken.None
            let stderrTask = Pump.drainDiscardOrEmpty host.Stderr CancellationToken.None
            let! outcome = waitWithTimeout ()
            // Observe both drains together so an I/O fault on one can't orphan the other.
            do! Task.WhenAll([| stdoutTask; stderrTask |])
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

            let stdoutTask = Pump.drainDiscardOrEmpty host.Stdout CancellationToken.None
            let stderrTask = Pump.drainDiscardOrEmpty host.Stderr CancellationToken.None

            // Capture a fault rather than letting it escape immediately, so the sampler is ALWAYS
            // cancelled and awaited before its CTS is disposed at scope exit — never left running as
            // an unobserved task. (A task CE cannot `do!` inside a `finally`, so this is the
            // try/with-then-single-cleanup form of try/finally; the cleanup must also precede reading
            // the sampler's metrics on the success path, which a `finally`/`use` could not guarantee.)
            let mutable error: exn option = None
            let mutable outcome = Unchecked.defaultof<Outcome>

            try
                let! settled = waitWithTimeout ()
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
    // pipes; true once the stdout streaming session is (or already was) ours.
    member private _.StartStdoutStreaming() : bool =
        if consumption = Consumption.StdoutStreaming then
            true
        elif not (claimStreaming Consumption.StdoutStreaming) then
            false
        else
            let stderrBuffer = Pump.LineBuffer(config.OutputBuffer)
            stderrStreamBuffer <- stderrBuffer

            let stdoutPump =
                task {
                    try
                        do!
                            pumpLines host.Stdout config.StdoutEncoding config.StdoutTee (fun line ->
                                invokeLine config.OnStdoutLine line
                                stdoutLineCount <- stdoutLineCount + 1

                                writeStreamItem
                                    stdoutChannel.Writer
                                    stdoutChannel.Reader
                                    (fun () -> stdoutLineCount)
                                    bumpDroppedStreamLine
                                    line)

                        stdoutChannel.Writer.Complete()
                    with ex ->
                        // A pump fault — a throwing `OnStdoutLine` handler, or `StreamFullMode.Error`
                        // tripping its cap — must still complete the channel, carrying the error, so a
                        // `StdoutLinesAsync` consumer observes it instead of hanging on a reader that
                        // never ends. Re-raise (preserving the original stack; `reraise` is unavailable
                        // inside a task CE) so `streamOutcome` / `FinishAsync` surface the same fault.
                        stdoutChannel.Writer.Complete ex
                        ExceptionDispatchInfo.Throw ex
                }

            let stderrPump =
                pumpLines host.Stderr config.StderrEncoding config.StderrTee (fun line ->
                    invokeLine config.OnStderrLine line
                    stderrLineCount <- stderrLineCount + 1
                    stderrBuffer.Add line
                    ValueTask.CompletedTask)

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

            true

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
    // pipes; true once the event streaming session is (or already was) ours.
    member private _.StartEventStreaming() : bool =
        if consumption = Consumption.EventStreaming then
            true
        elif not (claimStreaming Consumption.EventStreaming) then
            false
        else
            // Each pump completes the shared event channel on its own fault (carrying the error), so an
            // `OutputEventsAsync` consumer observes a throwing handler promptly rather than hanging until the
            // process exits — `eventOutcome` below only completes the channel after the exit wait, which
            // for a long-running child can be far away. `TryComplete` because the two pumps and the
            // combined task below all race to complete the one channel; re-raise so `eventOutcome` faults.
            // One helper for both streams so the fault-completion invariant lives in a single place.
            let eventPump
                (stream: Stream option)
                encoding
                tee
                (onLine: Action<string> option)
                (bump: unit -> unit)
                (countSoFar: unit -> int)
                (wrap: OutputLine -> OutputEvent)
                =
                task {
                    try
                        do!
                            pumpLines stream encoding tee (fun line ->
                                invokeLine onLine line
                                bump ()

                                writeStreamItem
                                    eventChannel.Writer
                                    eventChannel.Reader
                                    countSoFar
                                    bumpDroppedStreamLine
                                    (wrap (OutputLine line)))
                    with ex ->
                        eventChannel.Writer.TryComplete ex |> ignore
                        ExceptionDispatchInfo.Throw ex
                }

            let stdoutPump =
                eventPump
                    host.Stdout
                    config.StdoutEncoding
                    config.StdoutTee
                    config.OnStdoutLine
                    (fun () -> stdoutLineCount <- stdoutLineCount + 1)
                    (fun () -> stdoutLineCount)
                    OutputEvent.Stdout

            let stderrPump =
                eventPump
                    host.Stderr
                    config.StderrEncoding
                    config.StderrTee
                    config.OnStderrLine
                    (fun () -> stderrLineCount <- stderrLineCount + 1)
                    (fun () -> stderrLineCount)
                    OutputEvent.Stderr

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

            true

    /// Stream merged stdout+stderr line events as they arrive.
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
                    let mutable found = None

                    while found.IsNone do
                        let! line = stdoutChannel.Reader.ReadAsync linked.Token

                        if predicate.Invoke line then
                            found <- Some line

                    return Ok found.Value
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
    /// (or `Cancelled` if `cancellationToken` fires first). Unlike `WaitForLineAsync`, this does not read
    /// the child's stdout/stderr while polling — a child that floods a *piped* stream before it is
    /// ready can block on a full pipe; consume its output concurrently (`StdoutLinesAsync`/`OutputEventsAsync`)
    /// or run it with `Stdout`/`Stderr` set to `Inherit`/`Null` when polling a chatty process.
    member _.WaitForPortAsync
        (endpoint: IPEndPoint, timeout: TimeSpan, [<Optional>] cancellationToken: CancellationToken)
        : Task<Result<unit, ProcessError>> =
        ArgumentNullException.ThrowIfNull endpoint

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
                return Error(ProcessError.Cancelled config.Program)
            else
                return Error(ProcessError.NotReady(config.Program, timeout))
        }

    /// Poll `probe` until it returns true, or fail with `NotReady` after `timeout` (or `Cancelled`
    /// if `cancellationToken` fires first). Like `WaitForPortAsync`, this does not drain the child's
    /// stdout/stderr while polling — consume a piped, chatty child's output concurrently (or run it
    /// with `Stdout`/`Stderr` `Inherit`/`Null`) so it can't block on a full pipe before becoming ready.
    member _.WaitForAsync
        (probe: Func<Task<bool>>, timeout: TimeSpan, [<Optional>] cancellationToken: CancellationToken)
        : Task<Result<unit, ProcessError>> =
        ArgumentNullException.ThrowIfNull probe

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
                return Error(ProcessError.Cancelled config.Program)
            else
                return Error(ProcessError.NotReady(config.Program, timeout))
        }

    /// A memoized task that waits for the process to exit (draining its pipes) without reaping it —
    /// the racing primitive behind `WaitAnyAsync`/`WaitAllAsync`.
    member internal _.ExitTask: Task<Outcome> =
        if not exitStarted then
            exitStarted <- true

            exitTaskValue <-
                if consumption = Consumption.StdoutStreaming then
                    streamOutcome
                elif consumption = Consumption.EventStreaming then
                    // The event pumps already drain both pipes; reuse their shared outcome rather than
                    // starting our own drains here, which would race a second reader on the same streams.
                    eventOutcome
                else
                    // Claim the buffered slot so a terminal verb called after WaitAny/WaitAll on the
                    // same handle is refused rather than racing a second reader on these pipes.
                    claimBuffered () |> ignore

                    task {
                        // These drains are fire-and-forget for a race loser the caller may dispose
                        // mid-drain, so they must complete quietly on teardown rather than fault unobserved.
                        let stdoutDrain =
                            Pump.drainDiscardOrEmptyUntilDone host.Stdout CancellationToken.None

                        let stderrDrain =
                            Pump.drainDiscardOrEmptyUntilDone host.Stderr CancellationToken.None

                        let! outcome = waitWithTimeout ()
                        do! Task.WhenAll([| stdoutDrain; stderrDrain |])
                        // Racing this handle to exit *is* its completion (conclude does not reap, so the
                        // no-reap contract holds), so a `WaitAny`/`WaitAll`-only run still records its
                        // exit/metrics/span and clears the in-flight mark. Once-guarded, so a terminal verb
                        // afterwards (already refused by the buffered claim above) can't double-count.
                        conclude outcome
                        return outcome
                    }

        exitTaskValue

    /// Wait for the first of `processes` to exit; returns its index and outcome. Does not reap any
    /// of them — dispose them yourself.
    static member WaitAnyAsync(processes: RunningProcess[]) : Task<Result<WaitAnyResult, ProcessError>> =
        task {
            if processes.Length = 0 then
                return Error(ProcessError.Unsupported "WaitAnyAsync requires at least one process")
            else
                let tasks = processes |> Array.map (fun p -> p.ExitTask)
                let! completed = Task.WhenAny tasks
                let index = tasks |> Array.findIndex (fun t -> obj.ReferenceEquals(t, completed))
                let! outcome = completed
                return Ok(WaitAnyResult(index, outcome))
        }

    /// Wait for all of `processes` to exit; returns their outcomes in order. Does not reap them.
    static member WaitAllAsync(processes: RunningProcess[]) : Task<Outcome[]> =
        processes |> Array.map (fun p -> p.ExitTask) |> Task.WhenAll

    interface IAsyncDisposable with
        member _.DisposeAsync() =
            disposalCts.Cancel()
            host.Teardown()
