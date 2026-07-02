namespace ProcessKit

open System
open System.Diagnostics
open System.IO
open System.Threading
open System.Threading.Tasks

/// One stage's terminal state inside a pipeline run.
type internal PipelineStage =
    { Program: string
      Outcome: Outcome
      Unchecked: bool
      Stderr: string
      OkCodes: int list }

/// The captured result of running a whole pipeline: the last stage's stdout, every stage's
/// terminal state (left-to-right), the wall-clock duration, and whether the pipeline timed out.
type internal PipelineCapture =
    { LastStdout: byte[]
      Stages: PipelineStage list
      Duration: TimeSpan
      TimedOut: bool }

/// Runs a multi-stage pipeline inside one fresh shared `ProcessGroup` — the staging/wiring/teardown
/// for the `Pipeline` type. Kept next to `Pipeline` (its only consumer) rather than inside
/// `ProcessGroup`, which it drives purely through that group's public/internal surface.
module internal PipelineRunner =

    /// Spawn every stage into one fresh shared group, wire each stage's stdout to the next stage's
    /// stdin (no shell involved), capture the last stage's stdout, and reap the whole tree on exit.
    /// Cancellation or the optional `timeout` hard-kill the tree.
    let run
        (commands: Command list)
        (timeout: TimeSpan option)
        (lastTee: Stream option)
        (cancellationToken: CancellationToken)
        : Task<Result<PipelineCapture, ProcessError>> =
        task {
            let stages = List.toArray commands

            if stages.Length = 0 then
                return Error(ProcessError.Spawn("<pipeline>", "a pipeline needs at least one stage"))
            elif cancellationToken.IsCancellationRequested then
                return Error(ProcessError.Cancelled stages[stages.Length - 1].Program)
            else
                match ProcessGroup.Create() with
                | Error error -> return Error error
                | Ok group ->
                    use group = group
                    let startedAt = Stopwatch.GetTimestamp()
                    use timeoutCts = new CancellationTokenSource()

                    use linkedCts =
                        CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token)

                    match timeout with
                    // Only arm the deadline when it fits a BCL timer; an over-long span is "no timeout"
                    // (a negative one was already rejected by `Pipeline.Timeout`). Guards CancelAfter
                    // against a synchronous out-of-range throw that would abort the run mid-spawn.
                    | Some duration when Timeouts.isArmable duration -> timeoutCts.CancelAfter duration
                    | _ -> ()

                    use _registration = linkedCts.Token.Register(fun () -> group.KillTree())

                    // Dispose a pipe stream, swallowing the teardown-race exceptions (double close, or
                    // a broken pipe surfaced while flushing on dispose because the peer is gone).
                    let closeQuietly = Pump.disposeQuietly

                    let spawned = ResizeArray<Native.Spawned>()
                    let copyTasks = ResizeArray<Task>()
                    let stderrTasks = ResizeArray<Task<byte[]>>()
                    let mutable prevStdout: Stream option = None
                    let mutable spawnError = None
                    let mutable index = 0

                    while index < stages.Length && spawnError.IsNone do
                        // The pipeline owns stdout wiring: every stage's stdout must be a pipe —
                        // intermediate stages feed the next stage's stdin and the last stage's stdout is
                        // captured. Force `Piped` over any user-set `Null`/`Inherit` so an unwired
                        // downstream stdin can't deadlock the chain. Stages after the first also take
                        // their stdin from the previous stage's stdout, so they need a stdin pipe;
                        // `KeepStdinOpen` creates one without an auto-fed source (we copy `prevStdout`
                        // into it below).
                        let piped = stages[index] |> Command.stdout StdioMode.Piped

                        let stage = if index > 0 then piped.KeepStdinOpen() else piped

                        match group.SpawnInto stage with
                        | Error error -> spawnError <- Some error
                        | Ok sp ->
                            spawned.Add sp

                            if index = 0 then
                                // Only the first stage may carry its own stdin source; feed it. A
                                // pipeline does not surface a stage-0 stdin-source failure as
                                // `ProcessError.Stdin` (that is a single-command verb concern), so the
                                // feed task's observed-fault result is discarded here.
                                Pump.feedStdinSource sp.Stdin stages[0].Config.StdinSource |> ignore
                            else
                                match prevStdout, sp.Stdin with
                                | Some upstream, Some downstream ->
                                    copyTasks.Add(
                                        task {
                                            try
                                                do! upstream.CopyToAsync downstream
                                            with _ ->
                                                // A downstream stage that exits early stops reading
                                                // (broken pipe), or the stream is torn down during
                                                // teardown. Fall through to close both ends.
                                                ()

                                            // Close the write end (EOF to the downstream stage) AND the
                                            // upstream read end: if the downstream exited early, closing
                                            // the read end propagates a broken pipe up to the producing
                                            // stage (SIGPIPE / failed write) so it stops instead of
                                            // blocking forever on a full stdout pipe.
                                            closeQuietly downstream
                                            closeQuietly upstream
                                        }
                                        :> Task
                                    )
                                | _ -> ()

                            // Drain every stage's stderr so a full stderr pipe never blocks a stage.
                            stderrTasks.Add(Pump.drainRawOrEmpty sp.Stderr None CancellationToken.None)

                            prevStdout <- sp.Stdout

                        index <- index + 1

                    match spawnError with
                    | Some error ->
                        // Some stages started before the failure. Hard-kill them, then reap (waitpid)
                        // each, drain/observe the relay + stderr tasks, and close every pipe — the
                        // group's `use` dispose alone hard-kills but does not waitpid POSIX leaders or
                        // close these parent-side streams, so without this they would leak.
                        group.KillTree()

                        let! _ =
                            spawned
                            |> Seq.map (fun sp -> group.WaitHandle sp.Handle)
                            |> Seq.toArray
                            |> Task.WhenAll

                        for t in copyTasks do
                            do! t

                        let! _ = Task.WhenAll(stderrTasks.ToArray())

                        for sp in spawned do
                            Pump.closeSpawned sp

                        return Error error
                    | None ->
                        let lastSpawned = spawned[spawned.Count - 1]

                        let captureTask =
                            Pump.drainRawOrEmpty lastSpawned.Stdout lastTee CancellationToken.None

                        let waitTasks =
                            spawned |> Seq.map (fun sp -> group.WaitHandle sp.Handle) |> Seq.toArray

                        let! outcomes = Task.WhenAll waitTasks
                        do! Task.WhenAll(copyTasks.ToArray())
                        let! lastStdout = captureTask
                        let! stderrBytes = Task.WhenAll(stderrTasks.ToArray())

                        for sp in spawned do
                            Pump.closeSpawned sp

                        let duration = Stopwatch.GetElapsedTime startedAt

                        if cancellationToken.IsCancellationRequested then
                            return Error(ProcessError.Cancelled stages[stages.Length - 1].Program)
                        else
                            let stageResults =
                                [ for i in 0 .. stages.Length - 1 ->
                                      { Program = stages[i].Program
                                        Outcome = outcomes[i]
                                        Unchecked = stages[i].Config.UncheckedInPipe
                                        Stderr = stages[i].Config.StderrEncoding.GetString stderrBytes[i]
                                        OkCodes = stages[i].Config.OkCodes } ]

                            return
                                Ok
                                    { LastStdout = lastStdout
                                      Stages = stageResults
                                      Duration = duration
                                      TimedOut = timeoutCts.IsCancellationRequested }
        }
