namespace ProcessKit

open System
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks

/// A snapshot of a process group's resource usage.
///
/// `TotalCpuTime` and `PeakMemoryBytes` are `None` when the platform can't report them — the POSIX
/// process-group mechanism (macOS and the Linux fallback) has no kernel accumulator; the Linux
/// cgroup v2 backend (the `limits` feature) supplies them. Sealed with an internal constructor so
/// it can gain metrics without breaking the frozen API.
[<Sealed>]
type ProcessGroupStats internal (activeProcessCount: int, totalCpuTime: TimeSpan option, peakMemoryBytes: int64 option)
    =

    /// Number of live processes currently in the group. Under the POSIX process-group mechanism
    /// this counts live process *groups* (one per contained child) rather than individual
    /// processes; with a Job Object (or cgroup) it is the exact process count.
    member _.ActiveProcessCount = activeProcessCount

    /// Total CPU time (user + kernel) accumulated by the group, if available. On Windows this is
    /// cumulative across every process that has ever been in the Job (including terminated ones).
    member _.TotalCpuTime = totalCpuTime

    /// Peak memory used by the group in bytes, if available — the OS's own group-wide measure
    /// (Windows: the Job's peak *committed* memory). Not directly comparable across platforms.
    member _.PeakMemoryBytes = peakMemoryBytes

/// Resource summary of one finished run — produced by `RunningProcess.ProfileAsync`.
///
/// CPU and memory come from the started child process (the same source as `RunningProcess.CpuTime`
/// / `PeakMemoryBytes`), so they are `None` where per-process metrics are unavailable or when the
/// run exited before the first sample landed. Sealed with an internal constructor.
[<Sealed>]
type RunProfile
    internal
    (exitCode: int option, duration: TimeSpan, cpuTime: TimeSpan option, peakMemoryBytes: int64 option, samples: int) =

    /// The exit code; `None` for a run killed by its timeout or a signal.
    member _.ExitCode = exitCode

    /// Wall-clock time from process start until the run finished (exit reaped and output drained).
    member _.Duration = duration

    /// Cumulative CPU time (user + kernel) at the last successful sample.
    member _.CpuTime = cpuTime

    /// Peak resident memory observed across the samples, in bytes.
    member _.PeakMemoryBytes = peakMemoryBytes

    /// How many sampling ticks ran (including ones that found no data).
    member _.Samples = samples

    /// Average CPU utilisation over the run, in cores (`0.5` = half a core busy on average; can
    /// exceed `1.0` for multi-threaded children). `None` when CPU time was never observed or the
    /// run had no duration.
    member _.AvgCpu =
        match cpuTime with
        | Some cpu when duration > TimeSpan.Zero -> Some(cpu.TotalSeconds / duration.TotalSeconds)
        | _ -> None

/// A **pull-based** periodic `ProcessGroupStats` series (the iterator behind `ProcessGroup.SampleStatsAsync`):
/// the first sample lands on the first `MoveNextAsync`, then one per `period`. It samples only when
/// pulled and runs no background task, so abandoning it does no work and — crucially — does not keep
/// the group alive, preserving kill-on-drop. The series ends on the first failing snapshot (e.g.
/// after the group is released) or when `cancellationToken` fires.
type internal StatsSampler
    (sample: unit -> Result<ProcessGroupStats, ProcessError>, period: TimeSpan, cancellationToken: CancellationToken) =

    let mutable current = Unchecked.defaultof<ProcessGroupStats>
    let mutable first = true
    let mutable finished = false

    interface IAsyncEnumerator<ProcessGroupStats> with
        member _.Current = current

        member _.MoveNextAsync() : ValueTask<bool> =
            ValueTask<bool>(
                task {
                    if finished || cancellationToken.IsCancellationRequested then
                        finished <- true
                        return false
                    else
                        if first then
                            first <- false
                        else
                            try
                                // `period` is pre-clamped by the caller (`SampleStatsAsync`) into the armable
                                // range, so `Task.Delay` here can't throw on an over-long interval.
                                do! Task.Delay(period, cancellationToken)
                            with :? OperationCanceledException ->
                                finished <- true

                        if finished then
                            return false
                        else
                            match sample () with
                            | Ok snapshot ->
                                current <- snapshot
                                return true
                            | Error _ ->
                                finished <- true
                                return false
                }
            )

        member _.DisposeAsync() = ValueTask.CompletedTask

type internal StatsSamplerSeq(sample: unit -> Result<ProcessGroupStats, ProcessError>, period: TimeSpan) =
    interface IAsyncEnumerable<ProcessGroupStats> with
        member _.GetAsyncEnumerator(cancellationToken) =
            StatsSampler(sample, period, cancellationToken) :> IAsyncEnumerator<ProcessGroupStats>
