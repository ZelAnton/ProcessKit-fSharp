namespace ProcessKit

open System
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.Net
open System.Net.Sockets
open System.Runtime.ExceptionServices
open System.Threading
open System.Threading.Tasks
open System.Threading.Channels

/// The closures and state a `RunningProcess` is built from. Internal — `ProcessGroup.Start`
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
        /// Signal the tree to die without waiting (start_kill).
        StartKill: unit -> unit
        /// Gracefully kill the tree (SIGTERM, then SIGKILL after the grace period) without
        /// releasing the container — for timeouts.
        GracefulKill: TimeSpan -> Task
        /// Reap the tree and release the container.
        Teardown: unit -> ValueTask
    }

/// A live handle to a started process: stream its output, feed its stdin, wait for it, or
/// collect it to completion. Disposing it reaps the whole process tree (kill-on-drop).
[<Sealed>]
type RunningProcess internal (host: RunningHost) =

    let config = host.Config
    let mutable stdinTaken = false
    let mutable stdoutLineCount = 0
    let mutable stderrLineCount = 0
    let mutable streamingStarted = false
    let mutable stderrStreamBuffer = Unchecked.defaultof<Pump.LineBuffer>
    let mutable streamOutcome = Unchecked.defaultof<Task<Outcome>>
    let stdoutChannel = Channel.CreateUnbounded<string>()
    let eventChannel = Channel.CreateUnbounded<OutputEvent>()
    let mutable eventStreamingStarted = false
    let mutable exitStarted = false
    let mutable exitTaskValue = Unchecked.defaultof<Task<Outcome>>

    // Single-consumption guard. The output pipes can be pumped exactly once: a buffered one-shot verb
    // (OutputString/OutputBytes/Wait/Profile) consumes them whole, and the streaming verbs form one
    // session (StdoutLines/WaitForLine/Finish share the stdout channel; OutputEvents owns the event
    // channel). A second, different consumer would race two readers on the same pipe — splitting and
    // losing output — so it is refused. 0 = fresh, 1 = buffered one-shot, 2 = stdout streaming,
    // 3 = event streaming. Verb calls are assumed sequential (the class is not built for concurrent
    // verbs), so a plain flag matches the existing `streamingStarted`/`exitStarted` style.
    let mutable consumption = 0

    // Claim the pipes for a one-shot buffered verb — only from fresh (no re-entry: a second buffered
    // verb would re-pump already-torn-down streams).
    let claimBuffered () =
        if consumption = 0 then
            consumption <- 1
            true
        else
            false

    // Claim the pipes for a streaming session, allowing re-entry of the *same* streaming mode (so
    // StdoutLines → Finish, or repeated StdoutLines, compose) but rejecting a different consumer.
    let claimStreaming (mode: int) =
        if consumption = 0 then
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

    // Per-process CPU / peak-memory via the BCL `Process` (reads /proc on Linux, the OS APIs
    // elsewhere) — no metrics once the child has exited or where the platform does not report them.
    let processMetrics (pid: int) : TimeSpan option * uint64 option =
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
                    if peak > 0L then Some(uint64 peak) else None
                with _ ->
                    // Peak working set unavailable (some platforms report 0 / throw); omit it.
                    None

            cpu, memory
        with _ ->
            // The process has already exited or is inaccessible — no metrics to read.
            None, None

    let pumpToBuffer (stream: Stream) encoding tee (callback: Action<string> option) counter =
        task {
            let buffer = Pump.LineBuffer(config.OutputBuffer)

            let onLine (line: string) =
                callback |> Option.iter (fun cb -> cb.Invoke line)
                counter ()
                buffer.Add line

            do! Pump.readLines stream encoding tee onLine CancellationToken.None
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
    let pumpLines (stream: Stream option) encoding tee (onLine: string -> unit) =
        task {
            match stream with
            | Some s -> do! Pump.readLinesUntilDone s encoding tee onLine CancellationToken.None
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

    // Wait for exit, applying a configured timeout: on the deadline, kill the tree (gracefully if
    // `TimeoutGrace` is set, else hard) and report `Outcome.TimedOut`.
    let waitWithTimeout () : Task<Outcome> =
        match config.Timeout with
        // Arm the deadline only when it fits a BCL timer. A negative timeout was already rejected by
        // `Command.Timeout`, and `isArmable` screens out both negatives and over-long spans, so the
        // `Task.Delay` below can never throw synchronously and orphan the in-flight pumps. Anything
        // unarmable (no timeout, or "effectively never") just waits.
        | Some timeout when Timeouts.isArmable timeout ->
            task {
                let waitTask = host.Wait()
                let waitBase = waitTask :> Task
                use timeoutCts = new CancellationTokenSource()
                let timeoutDelay = Task.Delay(timeout, timeoutCts.Token)
                let! winner = Task.WhenAny(waitBase, timeoutDelay)

                if obj.ReferenceEquals(winner, waitBase) then
                    timeoutCts.Cancel()
                    return! waitTask
                else
                    match config.TimeoutGrace with
                    | Some grace -> do! host.GracefulKill grace
                    | None -> host.StartKill()

                    let! _ = waitTask
                    Log.timeout config.Logger config.Program timeout
                    return Outcome.TimedOut
            }
        | _ -> host.Wait()

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
            member _.DisposeAsync() = host.Teardown() }

    // Log the spawn once, at construction.
    do Log.spawn config.Logger config.Program host.Pid

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
    member _.PeakMemoryBytes: uint64 option =
        match host.Pid with
        | Some pid -> snd (processMetrics pid)
        | None -> None

    /// Total stdout lines pumped so far (counts dropped lines too).
    member _.StdoutLineCount = stdoutLineCount

    /// Total stderr lines pumped so far.
    member _.StderrLineCount = stderrLineCount

    /// Take the interactive stdin handle — `Some` only when the command kept stdin open without a
    /// source attached, and only once.
    member _.TakeStdin() : ProcessStdin option =
        match host.Stdin with
        | Some stream when config.KeepStdinOpen && config.StdinSource.IsNone && not stdinTaken ->
            stdinTaken <- true
            Some(ProcessStdin stream)
        | _ -> None

    /// Signal the process tree to die without waiting.
    member _.StartKill() = host.StartKill()

    /// Run to completion, capturing stdout as decoded text. A non-zero exit is data; the tree is
    /// reaped when the call returns.
    member _.OutputString() : Task<Result<ProcessResult<string>, ProcessError>> =
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
                Log.exit config.Logger config.Program outcome (elapsed ())

                if outBuf.TooLarge || errBuf.TooLarge then
                    return
                        Error(
                            tooLargeError
                                (outBuf.TotalLines + errBuf.TotalLines)
                                (outBuf.TotalBytes + errBuf.TotalBytes)
                        )
                else
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
    member _.OutputBytes() : Task<Result<ProcessResult<byte[]>, ProcessError>> =
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
                Log.exit config.Logger config.Program outcome (elapsed ())

                if errBuf.TooLarge then
                    return Error(tooLargeError errBuf.TotalLines errBuf.TotalBytes)
                else
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
    member _.Wait() : Task<Outcome> =
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
            Log.exit config.Logger config.Program outcome (elapsed ())
            return outcome
        }

    /// Run to completion while periodically sampling the child's CPU/memory every `interval`, and
    /// return a `RunProfile`. Drains and discards output (like `Wait`) and reaps the tree.
    member _.Profile(interval: TimeSpan) : Task<RunProfile> =
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
                            do! Task.Delay(period, sampleCts.Token)
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
            | None -> return RunProfile(outcome.Code, elapsed (), lastCpu, peakMemory, samples)
        }

    /// `Profile` sampling every 100 ms.
    member this.Profile() =
        this.Profile(TimeSpan.FromMilliseconds 100.0)

    // Returns false when a different consumption (a buffered verb, or event streaming) already owns the
    // pipes; true once the stdout streaming session is (or already was) ours.
    member private _.StartStdoutStreaming() : bool =
        if streamingStarted then
            true
        elif not (claimStreaming 2) then
            false
        else
            streamingStarted <- true
            let stderrBuffer = Pump.LineBuffer(config.OutputBuffer)
            stderrStreamBuffer <- stderrBuffer

            let stdoutPump =
                task {
                    try
                        do!
                            pumpLines host.Stdout config.StdoutEncoding config.StdoutTee (fun line ->
                                config.OnStdoutLine |> Option.iter (fun cb -> cb.Invoke line)
                                stdoutLineCount <- stdoutLineCount + 1
                                stdoutChannel.Writer.TryWrite line |> ignore)

                        stdoutChannel.Writer.Complete()
                    with ex ->
                        // A pump fault — most plausibly a throwing `OnStdoutLine` handler — must still
                        // complete the channel, carrying the error, so a `StdoutLines` consumer observes
                        // it instead of hanging on a reader that never ends. Re-raise (preserving the
                        // original stack; `reraise` is unavailable inside a task CE) so `streamOutcome`
                        // / `Finish` surface the same fault.
                        stdoutChannel.Writer.Complete ex
                        ExceptionDispatchInfo.Throw ex
                }

            let stderrPump =
                pumpLines host.Stderr config.StderrEncoding config.StderrTee (fun line ->
                    config.OnStderrLine |> Option.iter (fun cb -> cb.Invoke line)
                    stderrLineCount <- stderrLineCount + 1
                    stderrBuffer.Add line)

            streamOutcome <-
                task {
                    let! outcome = waitWithTimeout ()
                    // Await both pumps together so neither task is left unobserved if the other faults.
                    do! Task.WhenAll([| stdoutPump :> Task; stderrPump :> Task |])
                    return outcome
                }

            true

    /// Stream stdout line by line as it arrives. Call `Finish` afterwards for stderr + outcome.
    member this.StdoutLines() : IAsyncEnumerable<string> =
        if not (this.StartStdoutStreaming()) then
            raise (InvalidOperationException alreadyConsumedMessage)

        stdoutChannel.Reader.ReadAllAsync()

    /// After streaming stdout, wait for exit and return the captured stderr. Reaps the tree.
    member this.Finish() : Task<Result<Finished, ProcessError>> =
        if not (this.StartStdoutStreaming()) then
            Task.FromResult(Error(alreadyConsumedError ()))
        else

            task {
                use _reap = reapGuard ()
                let! outcome = streamOutcome
                Log.exit config.Logger config.Program outcome (elapsed ())

                if stderrStreamBuffer.TooLarge then
                    return Error(tooLargeError stderrStreamBuffer.TotalLines stderrStreamBuffer.TotalBytes)
                else
                    return Ok(Finished(outcome, stderrStreamBuffer.Text))
            }

    // Returns false when a different consumption (a buffered verb, or stdout streaming) already owns the
    // pipes; true once the event streaming session is (or already was) ours.
    member private _.StartEventStreaming() : bool =
        if eventStreamingStarted then
            true
        elif not (claimStreaming 3) then
            false
        else
            eventStreamingStarted <- true

            let stdoutPump =
                pumpLines host.Stdout config.StdoutEncoding config.StdoutTee (fun line ->
                    config.OnStdoutLine |> Option.iter (fun cb -> cb.Invoke line)
                    stdoutLineCount <- stdoutLineCount + 1
                    eventChannel.Writer.TryWrite(OutputEvent.Stdout(OutputLine line)) |> ignore)

            let stderrPump =
                pumpLines host.Stderr config.StderrEncoding config.StderrTee (fun line ->
                    config.OnStderrLine |> Option.iter (fun cb -> cb.Invoke line)
                    stderrLineCount <- stderrLineCount + 1
                    eventChannel.Writer.TryWrite(OutputEvent.Stderr(OutputLine line)) |> ignore)

            task {
                try
                    let! _ = waitWithTimeout ()
                    // Await both pumps together so neither is left unobserved if the other faults.
                    do! Task.WhenAll([| stdoutPump :> Task; stderrPump :> Task |])
                    eventChannel.Writer.Complete()
                with ex ->
                    // This combined pump is fire-and-forget, so a fault (e.g. a throwing handler) must
                    // complete the channel WITH the error: an `OutputEvents` consumer then observes it
                    // instead of hanging, and the fault is consumed here rather than surfacing as an
                    // unobserved task exception at finalization.
                    eventChannel.Writer.Complete ex
            }
            |> ignore

            true

    /// Stream merged stdout+stderr line events as they arrive.
    member this.OutputEvents() : IAsyncEnumerable<OutputEvent> =
        if not (this.StartEventStreaming()) then
            raise (InvalidOperationException alreadyConsumedMessage)

        eventChannel.Reader.ReadAllAsync()

    /// Wait until a stdout line satisfies `predicate`, or fail with `NotReady` after `timeout`.
    /// Consumed lines are not re-delivered; a later `StdoutLines`/`Finish` sees the rest.
    member this.WaitForLine(predicate: Func<string, bool>, timeout: TimeSpan) : Task<Result<string, ProcessError>> =
        if not (this.StartStdoutStreaming()) then
            Task.FromResult(Error(alreadyConsumedError ()))
        else

            task {
                use cts = new CancellationTokenSource(timeout)

                try
                    let mutable found = None

                    while found.IsNone do
                        let! line = stdoutChannel.Reader.ReadAsync cts.Token

                        if predicate.Invoke line then
                            found <- Some line

                    return Ok found.Value
                with
                | :? OperationCanceledException -> return Error(ProcessError.NotReady(config.Program, timeout))
                | :? ChannelClosedException -> return Error(ProcessError.NotReady(config.Program, timeout))
            }

    /// Wait until a TCP connection to `endpoint` succeeds, or fail with `NotReady` after `timeout`.
    member _.WaitForPort(endpoint: IPEndPoint, timeout: TimeSpan) : Task<Result<unit, ProcessError>> =
        task {
            let deadline = Stopwatch.StartNew()
            let mutable connected = false

            while not connected && deadline.Elapsed < timeout do
                use attempt = new CancellationTokenSource(TimeSpan.FromSeconds 1.0)

                try
                    use client = new TcpClient()
                    do! client.ConnectAsync(endpoint.Address, endpoint.Port, attempt.Token)
                    connected <- true
                with _ ->
                    // Any connection failure (refused / timed out / unreachable) just means the
                    // server is not up yet — back off and retry until the overall deadline.
                    try
                        do! Task.Delay(50, attempt.Token)
                    with :? OperationCanceledException ->
                        ()

            if connected then
                return Ok()
            else
                return Error(ProcessError.NotReady(config.Program, timeout))
        }

    /// Poll `probe` until it returns true, or fail with `NotReady` after `timeout`.
    member _.WaitFor(probe: Func<Task<bool>>, timeout: TimeSpan) : Task<Result<unit, ProcessError>> =
        task {
            let deadline = Stopwatch.StartNew()
            let mutable ready = false

            while not ready && deadline.Elapsed < timeout do
                let! result = probe.Invoke()

                if result then ready <- true else do! Task.Delay 50

            if ready then
                return Ok()
            else
                return Error(ProcessError.NotReady(config.Program, timeout))
        }

    /// A memoized task that waits for the process to exit (draining its pipes) without reaping it —
    /// the racing primitive behind `WaitAny`/`WaitAll`.
    member internal _.ExitTask: Task<Outcome> =
        if not exitStarted then
            exitStarted <- true

            exitTaskValue <-
                if streamingStarted then
                    streamOutcome
                else
                    task {
                        let stdoutDrain = Pump.drainDiscardOrEmpty host.Stdout CancellationToken.None
                        let stderrDrain = Pump.drainDiscardOrEmpty host.Stderr CancellationToken.None
                        let! outcome = waitWithTimeout ()
                        do! Task.WhenAll([| stdoutDrain; stderrDrain |])
                        return outcome
                    }

        exitTaskValue

    /// Wait for the first of `processes` to exit; returns its index and outcome. Does not reap any
    /// of them — dispose them yourself.
    static member WaitAny(processes: RunningProcess[]) : Task<Result<int * Outcome, ProcessError>> =
        task {
            if processes.Length = 0 then
                return Error(ProcessError.Unsupported "WaitAny requires at least one process")
            else
                let tasks = processes |> Array.map (fun p -> p.ExitTask)
                let! completed = Task.WhenAny tasks
                let index = tasks |> Array.findIndex (fun t -> obj.ReferenceEquals(t, completed))
                let! outcome = completed
                return Ok(index, outcome)
        }

    /// Wait for all of `processes` to exit; returns their outcomes in order. Does not reap them.
    static member WaitAll(processes: RunningProcess[]) : Task<Outcome[]> =
        processes |> Array.map (fun p -> p.ExitTask) |> Task.WhenAll

    interface IAsyncDisposable with
        member _.DisposeAsync() = host.Teardown()
