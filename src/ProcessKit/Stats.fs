namespace ProcessKit

open System
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks

[<Struct; NoComparison>]
type internal ProcessIoCounters =
    { ReadBytes: int64
      WriteBytes: int64
      ReadOperations: int64
      WriteOperations: int64 }

/// A snapshot of a process group's resource usage.
///
/// Optional peak-process, CPU, memory, and I/O fields are `None` when the platform can't report them —
/// the POSIX process-group mechanism (macOS and the Linux fallback) has no kernel accumulator; the
/// Linux cgroup v2 backend (the `limits` feature) supplies the controller metrics available to it.
/// Sealed with an internal constructor so it can gain metrics without breaking the frozen API.
[<Sealed>]
type ProcessGroupStats
    internal
    (
        activeProcessCount: int,
        peakProcessCount: int64 option,
        totalCpuTime: TimeSpan option,
        peakMemoryBytes: int64 option,
        ioCounters: ProcessIoCounters option
    ) =

    /// Number of live processes currently in the group. Under the POSIX process-group mechanism
    /// this counts live process *groups* (one per contained child) rather than individual
    /// processes — plus each individually tracked process adopted by pid (`ProcessGroup.AdoptByPid`),
    /// which is one process rather than a group; with a Job Object (or cgroup) it is the exact
    /// process count.
    member _.ActiveProcessCount = activeProcessCount

    /// Maximum number of kernel tasks (processes and their threads) charged to the group at once over
    /// its lifetime, if the containment mechanism exposes a native counter. Linux cgroup v2 reports
    /// `pids.peak` only when `MaxProcesses` is configured and the kernel is version 6.6 or later. This
    /// task count is not directly comparable with `ActiveProcessCount`, which counts process leaders.
    /// Windows Job Objects and the POSIX process-group fallback return `None`.
    member _.PeakProcessCount = peakProcessCount

    /// Total CPU time (user + kernel) accumulated by the group, if available. On Windows this is
    /// cumulative across every process that has ever been in the Job (including terminated ones).
    member _.TotalCpuTime = totalCpuTime

    /// Peak memory used by the group in bytes, if available — the OS's own group-wide measure
    /// (Windows: the Job's peak *committed* memory). Not directly comparable across platforms.
    member _.PeakMemoryBytes = peakMemoryBytes

    /// Bytes read by the contained tree, if the containment mechanism exposes an aggregate I/O
    /// counter. Windows reports the Job Object's OS I/O total; Linux cgroup v2 reports block-device
    /// bytes from `io.stat`; the POSIX process-group fallback returns `None`.
    member _.IoReadBytes = ioCounters |> Option.map _.ReadBytes

    /// Bytes written by the contained tree, with the same platform availability as `IoReadBytes`.
    member _.IoWriteBytes = ioCounters |> Option.map _.WriteBytes

    /// Read operations performed by the contained tree, when the platform exposes the count.
    member _.IoReadOperations = ioCounters |> Option.map _.ReadOperations

    /// Write operations performed by the contained tree, when the platform exposes the count.
    member _.IoWriteOperations = ioCounters |> Option.map _.WriteOperations

    member internal _.IoCounters = ioCounters

/// A point-in-time resource snapshot of one live `ProcessGroup` member.
///
/// `Pid` is the member identity from the group's native membership snapshot. CPU time and resident
/// memory are optional because the operating system may not expose them for every process/platform;
/// I/O counters are optional as a group because the per-process interface is not available everywhere.
/// Missing values remain `None` rather than being represented by fabricated zeroes. A member that exits
/// while it is being sampled is omitted from the returned list.
[<Sealed>]
type MemberStats
    internal
    (pid: int, cpuTime: TimeSpan option, residentMemoryBytes: int64 option, ioCounters: ProcessIoCounters option) =

    /// The process id from the group's point-in-time membership snapshot.
    member _.Pid = pid

    /// Cumulative user plus kernel CPU time, when the platform reports it for this member.
    member _.CpuTime = cpuTime

    /// Current resident memory (RSS/working set) in bytes, when the platform reports it.
    member _.ResidentMemoryBytes = residentMemoryBytes

    /// Bytes read by this member, when per-process I/O counters are available.
    member _.IoReadBytes = ioCounters |> Option.map _.ReadBytes

    /// Bytes written by this member, when per-process I/O counters are available.
    member _.IoWriteBytes = ioCounters |> Option.map _.WriteBytes

    /// Read operations performed by this member, when per-process I/O counters are available.
    member _.IoReadOperations = ioCounters |> Option.map _.ReadOperations

    /// Write operations performed by this member, when per-process I/O counters are available.
    member _.IoWriteOperations = ioCounters |> Option.map _.WriteOperations

/// Resource summary of one finished run — produced by `RunningProcess.ProfileAsync`.
///
/// CPU and memory come from the started child process (the same source as `RunningProcess.CpuTime`
/// / `PeakMemoryBytes`). I/O comes from the run's private containment tree and stays `None` for a
/// shared group, where an aggregate would include sibling runs. Sealed with an internal constructor.
[<Sealed>]
type RunProfile
    internal
    (
        outcome: Outcome,
        duration: TimeSpan,
        cpuTime: TimeSpan option,
        peakMemoryBytes: int64 option,
        ioCounters: ProcessIoCounters option,
        samples: int
    ) =

    /// How the profiled run concluded — a clean exit, a signal kill, or a timeout. `ExitCode` /
    /// `Signal` / `TimedOut` are the convenience reads over it, so a profile is a superset of `Wait`:
    /// one call yields both the telemetry and the outcome. A signal kill and a timeout both leave
    /// `ExitCode` `None` (a clean exit is `Some code`), so `Outcome` / `TimedOut` / `Signal` are how you
    /// tell those two apart.
    member _.Outcome = outcome

    /// The exit code; `None` for a run killed by its timeout or a signal (see `Outcome`).
    member _.ExitCode = outcome.Code

    /// The terminating signal number when the run was signal-killed (Unix); `None` otherwise.
    member _.Signal = outcome.Signal

    /// True when the run was killed by its timeout.
    member _.TimedOut = outcome.IsTimedOut

    /// Wall-clock time from process start until the run finished (exit reaped and output drained).
    member _.Duration = duration

    /// Cumulative CPU time (user + kernel) at the last successful sample.
    member _.CpuTime = cpuTime

    /// Peak resident memory observed across the samples, in bytes.
    member _.PeakMemoryBytes = peakMemoryBytes

    /// Bytes read by the run's whole private containment tree. `None` when the platform cannot expose
    /// a tree aggregate or the run belongs to a shared group whose counter would include siblings.
    member _.IoReadBytes = ioCounters |> Option.map _.ReadBytes

    /// Bytes written by the run's whole private containment tree, with the same availability contract
    /// as `IoReadBytes`.
    member _.IoWriteBytes = ioCounters |> Option.map _.WriteBytes

    /// Read operations performed by the run's private tree, when available.
    member _.IoReadOperations = ioCounters |> Option.map _.ReadOperations

    /// Write operations performed by the run's private tree, when available.
    member _.IoWriteOperations = ioCounters |> Option.map _.WriteOperations

    /// How many sampling ticks ran (including ones that found no data).
    member _.Samples = samples

    /// Average CPU utilisation over the run, **in cores** (`0.5` = half a core busy on average; can
    /// exceed `1.0` for multi-threaded children). `None` when CPU time was never observed or the
    /// run had no duration.
    member _.AvgCpuCores =
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
