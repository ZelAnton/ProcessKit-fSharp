namespace ProcessKit

open System
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.Net
open System.Net.Sockets
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

    let elapsed () =
        Stopwatch.GetElapsedTime host.StartedTimestamp

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
        | None -> host.Wait()
        | Some timeout ->
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
                    return Outcome.TimedOut
            }

    /// The pid, when known.
    member _.Pid = host.Pid

    /// When the process was started.
    member _.StartTime = host.StartTime

    /// Wall-clock time since the process started.
    member _.Elapsed = elapsed ()

    /// Total stdout lines pumped so far (counts dropped lines too).
    member _.StdoutLineCount = stdoutLineCount

    /// Total stderr lines pumped so far.
    member _.StderrLineCount = stderrLineCount

    /// Whether disposing this handle reaps the whole tree (always true).
    member _.KillsTreeOnDispose = true

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
        task {
            let stdoutTask = pumpStdoutBuffer ()
            let stderrTask = pumpStderrBuffer ()
            let! outcome = waitWithTimeout ()
            let! outBuf = stdoutTask
            let! errBuf = stderrTask
            do! host.Teardown()

            if outBuf.TooLarge || errBuf.TooLarge then
                return
                    Error(tooLargeError (outBuf.TotalLines + errBuf.TotalLines) (outBuf.TotalBytes + errBuf.TotalBytes))
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
        task {
            let stdoutTask =
                match host.Stdout with
                | Some s -> Pump.drainRaw s config.StdoutTee CancellationToken.None
                | None -> Task.FromResult Array.empty<byte>

            let stderrTask = pumpStderrBuffer ()
            let! outcome = waitWithTimeout ()
            let! stdoutBytes = stdoutTask
            let! errBuf = stderrTask
            do! host.Teardown()

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
        task {
            // Drain both pipes (so the child never blocks on a full buffer) without retaining.
            let stdoutTask =
                match host.Stdout with
                | Some s -> Pump.drainDiscard s CancellationToken.None
                | None -> Task.CompletedTask

            let stderrTask =
                match host.Stderr with
                | Some s -> Pump.drainDiscard s CancellationToken.None
                | None -> Task.CompletedTask

            let! outcome = waitWithTimeout ()
            do! stdoutTask
            do! stderrTask
            do! host.Teardown()
            return outcome
        }

    member private _.StartStdoutStreaming() =
        if not streamingStarted then
            streamingStarted <- true
            let stderrBuffer = Pump.LineBuffer(config.OutputBuffer)
            stderrStreamBuffer <- stderrBuffer

            let stdoutPump =
                task {
                    match host.Stdout with
                    | Some s ->
                        let onLine (line: string) =
                            config.OnStdoutLine |> Option.iter (fun cb -> cb.Invoke line)
                            stdoutLineCount <- stdoutLineCount + 1
                            stdoutChannel.Writer.TryWrite line |> ignore

                        do!
                            Pump.readLinesUntilDone
                                s
                                config.StdoutEncoding
                                config.StdoutTee
                                onLine
                                CancellationToken.None
                    | None -> ()

                    stdoutChannel.Writer.Complete()
                }

            let stderrPump =
                task {
                    match host.Stderr with
                    | Some s ->
                        let onLine (line: string) =
                            config.OnStderrLine |> Option.iter (fun cb -> cb.Invoke line)
                            stderrLineCount <- stderrLineCount + 1
                            stderrBuffer.Add line

                        do!
                            Pump.readLinesUntilDone
                                s
                                config.StderrEncoding
                                config.StderrTee
                                onLine
                                CancellationToken.None
                    | None -> ()
                }

            streamOutcome <-
                task {
                    let! outcome = waitWithTimeout ()
                    do! stdoutPump
                    do! stderrPump
                    return outcome
                }

    /// Stream stdout line by line as it arrives. Call `Finish` afterwards for stderr + outcome.
    member this.StdoutLines() : IAsyncEnumerable<string> =
        this.StartStdoutStreaming()
        stdoutChannel.Reader.ReadAllAsync()

    /// After streaming stdout, wait for exit and return the captured stderr. Reaps the tree.
    member this.Finish() : Task<Result<Finished, ProcessError>> =
        task {
            this.StartStdoutStreaming()
            let! outcome = streamOutcome
            do! host.Teardown()

            if stderrStreamBuffer.TooLarge then
                return Error(tooLargeError stderrStreamBuffer.TotalLines stderrStreamBuffer.TotalBytes)
            else
                return Ok(Finished(outcome, stderrStreamBuffer.Text))
        }

    member private _.StartEventStreaming() =
        if not eventStreamingStarted then
            eventStreamingStarted <- true

            let stdoutPump =
                task {
                    match host.Stdout with
                    | Some s ->
                        let onLine (line: string) =
                            config.OnStdoutLine |> Option.iter (fun cb -> cb.Invoke line)
                            stdoutLineCount <- stdoutLineCount + 1
                            eventChannel.Writer.TryWrite(OutputEvent.Stdout(OutputLine line)) |> ignore

                        do!
                            Pump.readLinesUntilDone
                                s
                                config.StdoutEncoding
                                config.StdoutTee
                                onLine
                                CancellationToken.None
                    | None -> ()
                }

            let stderrPump =
                task {
                    match host.Stderr with
                    | Some s ->
                        let onLine (line: string) =
                            config.OnStderrLine |> Option.iter (fun cb -> cb.Invoke line)
                            stderrLineCount <- stderrLineCount + 1
                            eventChannel.Writer.TryWrite(OutputEvent.Stderr(OutputLine line)) |> ignore

                        do!
                            Pump.readLinesUntilDone
                                s
                                config.StderrEncoding
                                config.StderrTee
                                onLine
                                CancellationToken.None
                    | None -> ()
                }

            task {
                let! _ = waitWithTimeout ()
                do! stdoutPump
                do! stderrPump
                eventChannel.Writer.Complete()
            }
            |> ignore

    /// Stream merged stdout+stderr line events as they arrive.
    member this.OutputEvents() : IAsyncEnumerable<OutputEvent> =
        this.StartEventStreaming()
        eventChannel.Reader.ReadAllAsync()

    /// Wait until a stdout line satisfies `predicate`, or fail with `NotReady` after `timeout`.
    /// Consumed lines are not re-delivered; a later `StdoutLines`/`Finish` sees the rest.
    member this.WaitForLine(predicate: Func<string, bool>, timeout: TimeSpan) : Task<Result<string, ProcessError>> =
        this.StartStdoutStreaming()

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
                        let stdoutDrain =
                            match host.Stdout with
                            | Some s -> Pump.drainDiscard s CancellationToken.None
                            | None -> Task.CompletedTask

                        let stderrDrain =
                            match host.Stderr with
                            | Some s -> Pump.drainDiscard s CancellationToken.None
                            | None -> Task.CompletedTask

                        let! outcome = waitWithTimeout ()
                        do! stdoutDrain
                        do! stderrDrain
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
