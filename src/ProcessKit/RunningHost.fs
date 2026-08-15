namespace ProcessKit

open System
open System.IO
open System.Threading.Tasks

/// The closures and state a `RunningProcess` is built from. Internal — `ProcessGroup.StartAsync`
/// constructs it, so `RunningProcess` need not reference `ProcessGroup` (no compile cycle).
///
/// It lives in its own file, ahead of the internal lifecycle modules that consume it
/// (`ConsumptionGate`, `RunTerminal`, `OutputSessions`) and of the `RunningProcess` facade itself:
/// F# compile order IS the dependency graph, so the record every one of them takes as its host
/// contract must be declared before the first of them.
type internal RunningHost =
    {
        Config: CommandConfig
        Pid: int option
        Stdout: Stream option
        Stderr: Stream option
        Stdin: Stream option
        StartTime: DateTime
        StartedTimestamp: int64
        /// The child's own OS-reported creation time (`Process.GetProcessById(pid).StartTime`),
        /// captured once right after spawn — the identity token `processMetrics` (T-097) re-checks
        /// before trusting a later `Process.GetProcessById pid` read, so a pid the OS recycled for an
        /// unrelated process after the child was reaped can't be mistaken for it. `None` when the pid
        /// is unknown, the identity read failed at spawn time (the child already exited, or the
        /// platform/timing raced it), or a synthetic host (tests/fakes) has no real process behind the
        /// pid — an unknown token is never proof either way, so the gate defers to the raw read exactly
        /// like the POSIX pgid identity check (`Native.Posix.processGroupStillTracked`) already does.
        StartTimeIdentity: DateTime option
        /// Wait for the process to exit and report how it concluded.
        Wait: unit -> Task<Outcome>
        /// The BOUNDED FINAL observation of the background stdin feeder's genuine source failure, made at
        /// the one moment a Result-producing verb decides an otherwise-successful run's result (the child
        /// exited with an accepted code and the output drains have finished). A feed that already finished
        /// answers immediately; a feed still reading its source gets a bounded window to conclude — so a
        /// slow source that only fails AFTER a fast child exited is reported as the real cause instead of
        /// being torn down unread — and is stopped, not awaited, once that window runs out. Never blocks
        /// past the budget, and never faults. A synthetic host with no feed to observe uses
        /// `RunningHost.NoStdinError`.
        StdinError: unit -> Task<exn option>
        /// Block until the background stdin feeder has finished draining the source, so `TakeStdin` never
        /// hands the caller a stream the feeder is still writing to (two concurrent writers on one pipe is
        /// forbidden). A no-op — returns immediately — when the command kept stdin open with **no** source
        /// (interactive from the start) or when there is nothing to feed, so only a `Stdin(source)` +
        /// `KeepStdinOpen` run actually waits. `TakeStdin` calls this OUTSIDE the consumption gate's lock.
        StdinFeedComplete: unit -> unit
        /// Signal the tree to die without waiting (start_kill).
        StartKill: unit -> unit
        /// Deliver a signal to this run's own contained process tree.
        Signal: Signal -> Result<unit, ProcessError>
        /// Gracefully kill the tree (configured soft signal, then SIGKILL after the grace period) without
        /// releasing the container — for timeouts.
        GracefulKill: TimeSpan -> Task
        /// Resize the child's pseudo-terminal to `(cols, rows)` — `Some` only for a `Command.Pty` run
        /// (the retained pseudoconsole handle / pty master fd from spawn), `None` otherwise. Backs
        /// `RunningProcess.ResizeAsync`, which returns a typed `ProcessError.Unsupported` when it is
        /// `None` (a non-PTY run — D6). A pure resize: it never touches the exit-wait/consumption state.
        ResizePty: (int * int -> Result<unit, ProcessError>) option
        /// Whole-tree stats for a run that owns a private containment group. Shared runs leave this
        /// `None`: the group's counters include siblings and must not be attributed to one profile.
        TreeStats: (unit -> ProcessGroupStats option) option
        /// Reap the tree and release the container.
        Teardown: unit -> ValueTask
    }

    /// The `StdinError` observer for a host with NO background stdin feed behind it: there is nothing to
    /// observe, so it answers "no fault" immediately and can never delay a verb. Used by the pipeline
    /// session's inner handle (stage 0's feed is observed by the chain itself, and reaches the session
    /// through the stashed capture's `Stdin0Error`) and by every synthetic host in the fakes and tests.
    static member NoStdinError() : Task<exn option> = Task.FromResult<exn option> None
