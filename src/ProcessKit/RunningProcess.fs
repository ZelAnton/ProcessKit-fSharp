namespace ProcessKit

open System
open System.Collections.Generic
open System.Diagnostics
open System.IO
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
            let! outcome = host.Wait()
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
                            outBuf.Truncated || errBuf.Truncated
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
            let! outcome = host.Wait()
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
                            errBuf.Truncated
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

            let! outcome = host.Wait()
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
                    let! outcome = host.Wait()
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
                let! _ = host.Wait()
                do! stdoutPump
                do! stderrPump
                eventChannel.Writer.Complete()
            }
            |> ignore

    /// Stream merged stdout+stderr line events as they arrive.
    member this.OutputEvents() : IAsyncEnumerable<OutputEvent> =
        this.StartEventStreaming()
        eventChannel.Reader.ReadAllAsync()

    interface IAsyncDisposable with
        member _.DisposeAsync() = host.Teardown()
