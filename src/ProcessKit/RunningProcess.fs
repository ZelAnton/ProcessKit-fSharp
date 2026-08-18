namespace ProcessKit

open System
open System.Collections.Generic
open System.Diagnostics
open System.Diagnostics.CodeAnalysis
open System.IO
open System.Net
open System.Net.Http
open System.Net.Sockets
open System.Runtime.ExceptionServices
open System.Runtime.InteropServices
open System.Text
open System.Text.Json
open System.Text.Json.Serialization.Metadata
open System.Threading
open System.Threading.Tasks
open System.Threading.Channels

/// The result of `RunningProcess.WaitAnyAsync`: which started process finished first and how it
/// concluded. A named type (rather than a tuple) so the fields read clearly from C#.
[<Sealed; NoComparison>]
type WaitAnyResult internal (index: int, outcome: Outcome) =

    /// The index, into the array passed to `WaitAnyAsync`, of the process that finished first.
    member _.Index = index

    /// How that process concluded.
    member _.Outcome = outcome

/// A live handle to a started process: stream its output, feed its stdin, wait for it, or
/// collect it to completion. Disposing it reaps the whole process tree (kill-on-drop).
[<Sealed>]
type RunningProcess
    internal (host: RunningHost, extraFdStreams: (int * Stream) list, recordedCompletion: (TimeSpan * bool) option) =

    let config = host.Config
    let hasPseudoTerminal = config.Pty.IsSome || host.ResizePty.IsSome
    let stdinTarget = ProcessStdinTarget.forRun hasPseudoTerminal

    // ---- what this handle is composed of ---------------------------------------------------------
    //
    // The lifecycle internals live in three internal modules, each owning one axis of it and exposing
    // a small contract to this type — which keeps every public verb, and every documented guarantee,
    // exactly where a consumer looks for it:
    //
    //   `ConsumptionGate` — the consumption-claim state machine and terminal-wait ledger: who owns the
    //     output pipes, which one-shot resources (each enumerator, the interactive stdin writer, each
    //     extra fd) have already been handed out, and the two memoized `Task<Outcome>`s every terminal
    //     path shares. It owns the ONE lock and runs each session's setup inside it, so a claim and the
    //     session it starts can never be observed half-done — including the terminal discard latch and
    //     the paired claim gate that keeps that drop honest (KB K-163).
    //   `RunTerminal` — the shared terminal waits and teardown: the configured timeout race, the
    //     bounded post-kill reap window, the bounded post-exit output drain, this handle's lifecycle
    //     tokens, and the reap guard every terminal verb reaps in.
    //   `OutputSessions` — the pumps, their streaming channels and counters, and the five session
    //     shapes a claimed handle can take. Platform-agnostic, and bounded only through `RunTerminal`.
    //
    // The verbs below COMPOSE those three; they never reach around them. So a new streaming or
    // lifecycle capability has one place to go, and the claim/latch invariants stay in one file.

    // The live monotonic time since THIS handle's spawn (`host.StartedTimestamp`) — the clock every
    // DEADLINE on this handle is measured against (`RunTerminal`, which is given this exact function
    // and no other clock), and the completion clock for every handle that is not replaying a recording.
    let sinceSpawn () =
        Stopwatch.GetElapsedTime host.StartedTimestamp

    // A cassette handle represents an already-completed run, so its COMPLETION clock must stay frozen
    // at the recorded value while ordinary live/fake handles continue reading their own stopwatch.
    // Completion metadata only — `Elapsed`, `ProcessResult.Duration`, `RunProfile`, telemetry: never a
    // deadline input. A frozen clock cannot bound anything (it does not advance, so every wait created
    // on such a handle would re-arm the same `Timeout - recordedDuration` remainder instead of resolving
    // to one absolute deadline), which is why `RunTerminal` is handed `sinceSpawn` above and not this.
    let elapsed () =
        match recordedCompletion with
        | Some(duration, _) -> duration
        | None -> sinceSpawn ()

    let recordedTruncated =
        match recordedCompletion with
        | Some(_, truncated) -> truncated
        | None -> false

    // The per-run correlation id: the verb layer stamps one (shared across a run's retries); a direct
    // spawn with none gets a fresh per-incarnation id. Carried on every run-scoped log/trace event.
    let runId =
        match config.RunId with
        | Some id -> id
        | None -> Diag.newRunId ()

    // Count the run as started + in-flight, and capture the ambient `Activity` now (at spawn) so the
    // backdated completion span nests under it. Runs once, at construction (like the spawn log). Defined
    // before `RunTerminal` below, which carries `runId` into the timeout log and `markAbandoned` into
    // its reap guard. The once-guarded conclude/abandon paths (formerly `conclude`/`markAbandoned` with
    // a hand-rolled `concludedFlag`) now live in the shared `RunTelemetryScope` (T-041) — single
    // consumption already means one terminal verb runs, but its once-guard makes that bulletproof, so
    // metrics can't double-count and a run never yields two spans. An abandoned run (spawned, never
    // driven to a terminal verb) simply isn't counted as completed.
    let telemetry = RunTelemetryScope.Start(config.Program, runId, host.StartTime)

    let conclude (outcome: Outcome) =
        telemetry.Conclude(config.Logger, outcome, host.Pid, elapsed ())

    // Clear the `runs.active` mark for a run whose handle is being disposed without ever having reached
    // a terminal verb (a streaming/event-driven handle the caller only consumed and dropped) — a no-op
    // once a terminal verb has already run (`telemetry`'s own once-guard).
    let markAbandoned () = telemetry.Abandon()

    // The claim state machine (see `ConsumptionGate`). It is given the stdin writer this run keeps open
    // — `host.Stdin` is `Some` exactly when the pipe is kept open, and `KeepStdinOpen` is what makes it
    // claimable at all: a source WITHOUT `KeepStdinOpen` closes the pipe after draining, so nothing is
    // handed out — plus the extra-fd channels, which are one-shot claims of the same family.
    let gate =
        ConsumptionGate((if config.KeepStdinOpen then host.Stdin else None), extraFdStreams)

    // The shared terminal waits and teardown for this handle (see `RunTerminal`).
    let terminal = RunTerminal(config, host, runId, sinceSpawn, markAbandoned)

    // This handle's own read ends: activity-tracked when an idle timeout is configured, and always
    // severable so the bounded post-exit output drain can end their pumps at a clean EOF.
    let stdoutStream = terminal.Severable(terminal.WatchActivity host.Stdout)
    let stderrStream = terminal.Severable(terminal.WatchActivity host.Stderr)

    // Start (or join) the ONE exit wait this handle ever performs. The wait itself is created exactly
    // once, under the gate's lock, and `RunTerminal.ObserveFault` is attached right there — at creation,
    // not per consumption (KB K-084): the readiness race below races this wait without ever awaiting it,
    // so on a probe-only handle (probe, then dispose, with no terminal verb) a fault from it would
    // otherwise surface as an unobserved task exception at finalization. The attach is purely
    // observational, so every real awaiter still gets and re-throws the original fault unchanged.
    let ensureBufferedWait () : Task<Outcome> =
        gate.EnsureBufferedWait(fun () ->
            let wait = terminal.WaitWithTimeout()
            RunTerminal.ObserveFault wait
            wait)

    // Everything that reads this handle's pipes (see `OutputSessions`). The claim decisions stay with
    // the gate: it calls into these session builders from inside its own lock.
    let sessions =
        OutputSessions(
            config,
            terminal,
            stdoutStream,
            stderrStream,
            ensureBufferedWait,
            conclude,
            (fun () -> gate.DiscardingStdoutStream)
        )

    let alreadyConsumedMessage = ConsumptionRefusal.message

    let alreadyConsumedError () = ConsumptionRefusal.error ()

    // Why a byte-exact stderr stream is impossible for THIS run, if it is — the one place that decides
    // it, so `StderrChunksAsync` refuses honestly (a typed `ProcessError.Unsupported`) instead of
    // handing back a stream that could only ever be empty. The condition is the ground truth (is there
    // a parent-side stderr pipe to read at all: `host.Stderr`), never the config alone — a test double
    // that models a merged run answers exactly as the spawn it stands in for. The config only explains
    // WHICH configuration removed the stream, so the message names the real cause.
    let stderrChunksUnsupported () : ProcessError option =
        match stderrStream with
        | Some _ -> None
        | None ->
            let reason =
                if config.MergeStderr then
                    "this run merges stderr into stdout (MergeStderr), so there is no separate stderr stream; stream the merged bytes with StdoutChunksAsync"
                elif hasPseudoTerminal then
                    "a PTY run gives the child one terminal device, so there is no separate stderr stream; stream the merged bytes with StdoutChunksAsync"
                elif config.StderrFile.IsSome then
                    "this run redirects stderr straight to a file, so there is no parent-side stderr stream to read"
                else
                    "this run does not pipe stderr, so there is no parent-side stderr stream to read"

            Some(ProcessError.Unsupported $"StderrChunksAsync: {reason}")

    // Hand `stdoutStream`/`stderrStream` to a readiness probe (`WaitForPortAsync`/`WaitForAsync`) for
    // its background drain — but only a still-`Fresh` handle's pipes: if a buffered verb or a
    // streaming session already claimed them, that consumer's own pump already drains them, and
    // handing the same streams to the probe as well would start a second, racing reader on the same
    // pipe. A snapshot read, not a claim (the gate's `IsFresh`, taken once before the probe's first
    // attempt): the consumption is left untouched, so a real verb can still claim the pipes normally
    // once the probe stops draining.
    let probeDrainStreams () : Stream option * Stream option =
        if gate.IsFresh then
            stdoutStream, stderrStream
        else
            None, None

    // The probe-vs-exit race (`ReadinessRace.raceAgainstExit`), bound to THIS handle's config, its
    // still-`Fresh` pipe snapshot and its one memoized exit wait. All five readiness verbs go through
    // this one choke point, so their early-exit behaviour cannot drift apart (KB K-043).
    let raceReadinessAgainstExit
        (timeout: TimeSpan)
        (cancellationToken: CancellationToken)
        (startProbe:
            ReadinessAttempts
                -> Stream option
                -> Stream option
                -> TimeSpan
                -> CancellationToken
                -> Task<Result<unit, ProcessError>>)
        : Task<Result<unit, ProcessError>> =
        ReadinessRace.raceAgainstExit config probeDrainStreams ensureBufferedWait timeout cancellationToken startProbe

    // End the child's input on behalf of a caller that kept stdin open (`Command.KeepStdinOpen`) and then
    // drove this run through a TERMINAL/consuming verb without ever taking the writer. Past such a verb
    // nobody can write that pipe any more — the buffered verbs run the child to completion, and the
    // high-level `RunAsync`/Parse/JSON/`FirstLine` verbs never hand the `RunningProcess` out at all — so a
    // child that reads its stdin to EOF would otherwise wait forever for an end of input that can no longer
    // come, and the verb would hang with it (to its timeout, or indefinitely without one).
    //
    // It makes the SAME once-only claim `TakeStdin` makes (`ConsumptionGate.TryClaimInteractiveStdin`), so
    // the two resolve to exactly one owner of the pipe with no double close and no kept-open pipe abandoned
    // behind a lost race.
    //
    // Deliberately NOT called on the ordinary `StartAsync` live-handle path: a caller holding the handle
    // still has `TakeStdin`, so streaming stdout/stderr, the readiness probes and the interactive sessions
    // leave the pipe exactly as they found it. Only a verb that ENDS the run claims here.
    let finishUnclaimedStdin () : unit =
        match gate.TryClaimInteractiveStdin() with
        | None ->
            // Nothing outstanding: this run keeps no stdin pipe open, or the caller already owns the writer
            // (`TakeStdin`/`TakeStdinAsync`, a `PtySession`/`ContentLengthSession`) and ends its own input
            // through `ProcessStdin.FinishAsync` — a verb must never close a handle it has given away, so
            // completion simply waits for that owner's own `FinishAsync`, exactly as before.
            ()
        | Some stream ->
            // The very handle `TakeStdin` would have returned, so the end of input goes out through the ONE
            // existing platform path (`ProcessStdin.FinishAsync`): a plain pipe end is closed, while a
            // stream that owns no handle to close delivers its terminal's own end-of-input gesture through
            // `Native.Common.IStdinFinisher` — the POSIX pty master view sends `termios.c_cc[VEOF]`, the
            // Windows ConPTY writer sends Ctrl-Z + Enter and leaves the session's host-input pipe open. A
            // bespoke `Dispose` here would release nothing on either PTY transport and leave the child
            // waiting on an EOF that can never arrive (see `Native.Common.IStdinFinisher`).
            let writer = ProcessStdin(stream, config.StdinEncoding, stdinTarget)

            let deliverEndOfInput () : Task =
                task {
                    try
                        // Symmetric with `TakeStdin`'s blocking claim: on a `Stdin(source)` + `KeepStdinOpen`
                        // run the background feeder is still this pipe's writer, so the child must receive
                        // the WHOLE source before its end of input — two writers on one pipe is forbidden,
                        // and ending it early would truncate the very input the caller asked to be fed.
                        // A no-op for an interactive-only run (no source, nothing to feed).
                        host.StdinFeedComplete()
                        do! writer.FinishAsync()
                    with _ ->
                        // Swallowed deliberately, and only here. This delivery is a courtesy made FOR a
                        // caller that never took the writer, on a detached task nothing awaits: there is no
                        // honest channel to report it through, and it must never displace the run's real
                        // result (an outcome, a capture, a stdin-SOURCE failure) with a fault of its own.
                        // `ProcessStdin.FinishAsync` already completes successfully for the two cases where
                        // the end of input is moot rather than lost — the child hung up its terminal, or
                        // this run's own teardown released the stream first — so what lands here is a
                        // genuine, rare delivery failure whose only effect is that the child keeps waiting
                        // for input exactly as it did before this helper existed; the verb's own
                        // timeout/cancellation/kill and the reap guard's teardown still bound the run.
                        ()
                }
                :> Task

            // Detached onto the thread pool, never awaited by the verb that started it. `StdinFeedComplete`
            // is blocking (the host exposes no async form) and a feeder can only finish once the child
            // consumes what it is being fed — which needs the verb's own drains to be running — so awaiting
            // this inline would make a verb's progress depend on work that depends on the verb: exactly the
            // deadlock the detachment avoids. It also means the helper adds no new wait window to bound
            // (KB K-149) and never touches the shared exit wait / reap-once gate (KB K-016): the child's
            // exit is still observed by the one memoized wait alone. The claim itself is synchronous, so
            // ownership is decided before this returns even though the delivery is not.
            Task.Run(deliverEndOfInput) |> ignore

    // Per-process CPU / peak-memory via the BCL `Process` (reads /proc on Linux, the OS APIs
    // elsewhere) — no metrics once the child has exited or where the platform does not report them.
    //
    // Gated by pid identity (T-097): `waitPosix` reaps the child as soon as it exits, before a verb
    // that later reads `CpuTime`/`PeakMemoryBytes`/`ProfileAsync`'s sampler necessarily observes that —
    // the OS is then free to recycle the pid for an unrelated process, and a raw `Process.GetProcessById
    // pid` read would silently hand back THAT stranger's metrics. `host.StartTimeIdentity` is this
    // child's own OS-reported creation time, captured once right after spawn; re-reading `proc.StartTime`
    // here and comparing catches a recycled pid (its own process, by definition, was created at a
    // different time) before any metric is read. An unknown identity on either side (no captured token,
    // or this platform's `Process.StartTime` throws) is never proof of a mismatch — the gate then defers
    // to the raw read, exactly like the POSIX pgid identity check already does for the tree-level
    // liveness probes.
    let processMetrics (pid: int) : TimeSpan option * int64 option =
        try
            use proc = Process.GetProcessById pid

            let identityMatches =
                match host.StartTimeIdentity with
                | Some captured ->
                    try
                        proc.StartTime = captured
                    with _ ->
                        // `StartTime` unreadable on this platform/timing — no current token to compare
                        // against; defer to the raw read rather than spuriously withholding real metrics.
                        true
                | None -> true

            if not identityMatches then
                // The pid answers, but it is not our child anymore — a recycled pid. Withhold the
                // stranger's metrics rather than silently misattributing them to this run.
                None, None
            else

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

    let tooLargeError (totalLines: int) (totalBytes: int) =
        ProcessError.OutputTooLarge(
            config.Program,
            config.OutputBuffer.MaxLines,
            config.OutputBuffer.MaxBytes,
            totalLines,
            totalBytes
        )

    // A genuine stdin-source failure surfaces as `ProcessError.Stdin` only on an otherwise-successful
    // run — an accepted exit code. A non-zero/unaccepted exit, a signal, or a timeout is the "realer"
    // failure and wins: the outcome passes through unchanged so the caller's own classifier sees it. (A
    // cancelled run is already turned into `ProcessError.Cancelled` upstream, before this is reached.)
    //
    // Called by each Result-producing verb at its ONE classification point, after the exit outcome AND
    // the output drains have been awaited. Only then is the feeder observed, and only on the success
    // branch — which is both the correct precedence and why a failing/timed-out run pays nothing for the
    // bounded window: `host.StdinError` waits (bounded) for a source still reading when the child exited,
    // instead of peeking once and calling a lost race a success. A feed that already finished answers with
    // no wait at all — a synchronous source failure, e.g. a missing `FromFile`, has nothing left to read and
    // is finished once it has ended the child's stdin.
    let stdinErrorOnSuccess (outcome: Outcome) : Task<ProcessError option> =
        if outcome.IsAcceptedBy config.OkCodes then
            task {
                let! fault = host.StdinError()
                return fault |> Option.map (fun ex -> ProcessError.Stdin(config.Program, ex.Message))
            }
        else
            Task.FromResult<ProcessError option> None

    let waitForHttp
        (uri: Uri)
        (isSatisfactory: Func<HttpResponseMessage, bool>)
        (timeout: TimeSpan)
        (cancellationToken: CancellationToken)
        : Task<Result<unit, ProcessError>> =
        raceReadinessAgainstExit timeout cancellationToken (fun attempts stdout stderr budget readinessToken ->
            ReadinessProbe.waitForHttp
                config.TimeProvider
                config.Program
                stdout
                stderr
                uri
                isSatisfactory
                attempts
                budget
                readinessToken)

    let waitForHttpWithClient
        (client: HttpClient)
        (uri: Uri)
        (isSatisfactory: Func<HttpResponseMessage, bool>)
        (timeout: TimeSpan)
        (cancellationToken: CancellationToken)
        : Task<Result<unit, ProcessError>> =
        raceReadinessAgainstExit timeout cancellationToken (fun attempts stdout stderr budget readinessToken ->
            ReadinessProbe.waitForHttpWithClient
                config.TimeProvider
                config.Program
                stdout
                stderr
                client
                uri
                isSatisfactory
                attempts
                budget
                readinessToken)

    let httpStatusPredicate (acceptableStatusCodes: seq<int>) =
        ArgumentNullException.ThrowIfNull acceptableStatusCodes
        let accepted = HashSet<int>(acceptableStatusCodes)

        if accepted.Count = 0 then
            raise (
                ArgumentException("At least one acceptable HTTP status code is required.", nameof acceptableStatusCodes)
            )

        Func<HttpResponseMessage, bool>(fun response -> accepted.Contains(int response.StatusCode))

    let waitForPort
        (endpoint: IPEndPoint)
        (timeout: TimeSpan)
        (cancellationToken: CancellationToken)
        : Task<Result<unit, ProcessError>> =
        raceReadinessAgainstExit timeout cancellationToken (fun attempts stdout stderr budget readinessToken ->
            ReadinessProbe.waitForPort
                config.TimeProvider
                config.Program
                stdout
                stderr
                endpoint
                attempts
                budget
                readinessToken)

    let waitForSocket
        (endpoint: EndPoint)
        (timeout: TimeSpan)
        (cancellationToken: CancellationToken)
        : Task<Result<unit, ProcessError>> =
        raceReadinessAgainstExit timeout cancellationToken (fun attempts stdout stderr budget readinessToken ->
            ReadinessProbe.waitForSocket
                config.TimeProvider
                config.Program
                stdout
                stderr
                endpoint
                attempts
                budget
                readinessToken)

    let waitForCustom
        (probe: Func<Task<bool>>)
        (timeout: TimeSpan)
        (cancellationToken: CancellationToken)
        : Task<Result<unit, ProcessError>> =
        raceReadinessAgainstExit timeout cancellationToken (fun attempts stdout stderr budget readinessToken ->
            ReadinessProbe.waitFor
                config.TimeProvider
                config.Program
                stdout
                stderr
                probe
                attempts
                budget
                readinessToken)

    // Log the spawn once, at construction. Both this `Log.spawn` and the `RunTelemetryScope.Start`
    // (`Diag.runStarted`) above swallow any fault the consumer's logger / metric / trace sink raises, so
    // constructing this handle can never throw *from observability*. That is what closes the ownership
    // window between the native spawn (already done inside `host`) and the hand-off to the caller: the
    // freshly-spawned tree's deterministic owner — this handle — is always successfully constructed and
    // returned, so a broken logger can never orphan the child here. The runner's construction site
    // (`JobRunner.start`) adds a defence-in-depth teardown as a backstop for any non-observability fault.
    do Log.spawn config.Logger config.Program host.Pid runId

    internal new(host: RunningHost) = RunningProcess(host, [], None)

    internal new(host: RunningHost, extraFdStreams: (int * Stream) list) = RunningProcess(host, extraFdStreams, None)

    internal new(host: RunningHost, recordedDuration: TimeSpan, recordedTruncated: bool) =
        RunningProcess(host, [], Some(recordedDuration, recordedTruncated))

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

    /// Whole-tree peak memory for internal resource monitors. A private group may expose accounting
    /// even when the leader has exited; shared/fallback groups fail honestly instead of attributing a
    /// sibling aggregate or silently substituting leader-only memory.
    member internal _.TreePeakMemoryBytes() : Result<int64, ProcessError> =
        try
            match host.TreeStats with
            | None -> Error(ProcessError.Unsupported "whole-tree memory accounting is unavailable for this run")
            | Some snapshot ->
                match snapshot () with
                | None -> Error(ProcessError.Unsupported "whole-tree memory accounting could not be read for this run")
                | Some stats ->
                    match stats.PeakMemoryBytes with
                    | Some bytes -> Ok bytes
                    | None ->
                        Error(ProcessError.Unsupported "whole-tree memory accounting is unavailable on this platform")
        with ex ->
            Error(ProcessError.Unsupported $"whole-tree memory accounting failed for this run: {ex.Message}")

    /// Total stdout lines pumped so far (counts dropped lines too).
    member _.StdoutLineCount = sessions.StdoutLineCount

    /// Total stderr lines pumped so far.
    member _.StderrLineCount = sessions.StderrLineCount

    /// Stream items dropped so far by a bounded streaming policy's `StreamFullMode.DropOldest`/
    /// `DropNewest` (always `0` unless `Command.StreamBuffer` is configured with one of those modes).
    /// For line/event streams this counts dropped lines/events; for `StdoutChunksAsync`/
    /// `StderrChunksAsync` it counts dropped chunks. It is the streaming analogue of a buffered verb's
    /// `ProcessResult.Truncated`.
    member _.DroppedStreamLineCount = sessions.DroppedStreamLineCount

    /// Take the parent side of the POSIX full-duplex channel connected to `targetFd` in the child.
    /// Returns `Some` only for a descriptor configured with `Command.ExtraFd`, and only once.
    member _.TakeExtraFd(targetFd: int) : Stream option =
        if targetFd < 3 then
            invalidArg (nameof targetFd) "An extra child file descriptor must be at least 3."

        gate.TryTakeExtraFd targetFd

    /// Take the interactive stdin handle — `Some` only when the command kept stdin open
    /// (`Command.KeepStdinOpen`), and only once. `None` in every other case: stdin was not kept open (no
    /// `KeepStdinOpen`, or an `InheritStdin` child), the writer was already taken (an earlier `TakeStdin`,
    /// or the `PtySession`/`ContentLengthSession` that took it for its own send verbs), or a verb that ran
    /// this handle to completion found the writer untaken and ended the child's input itself (see
    /// `FinishUnclaimedStdin` — the same once-only claim, made from the other side). So take the writer
    /// BEFORE `OutputStringAsync`/`OutputBytesAsync`/`WaitAsync`/`ProfileAsync`, a first-consumer
    /// `WaitAnyAsync`/`WaitAllAsync`/`StopAsync`, or a runner-level verb that hands out no handle at all:
    /// such a verb claims as it starts, and a claim lost to it is not recoverable — the child's end of
    /// input is already on its way. A writer this call hands out stays the caller's: no verb closes a
    /// handle it gave away, and completion waits for that caller's own `FinishAsync`.
    ///
    /// With **no** source the writer is available immediately; with a `Command.Stdin(source)` it is
    /// available once the background feeder has finished draining that source
    /// (this call blocks until then), so the caller never writes to the pipe while the feeder still is.
    /// That wait is deadlock-safe even on a single-threaded `SynchronizationContext` (a WPF/WinForms UI
    /// thread, classic ASP.NET): the source feeder runs detached on the thread pool (see
    /// `Pump.feedStdin`'s `backgroundTask`), so it always makes progress while this thread is blocked here
    /// and is never waiting to post a continuation back to it.
    member _.TakeStdin() : ProcessStdin option =
        match gate.TryClaimInteractiveStdin() with
        | Some stream ->
            // Wait — OUTSIDE the claim lock, so it never blocks other verbs — for the source feeder to finish
            // before handing the stream over. A no-op when there is no source (interactive-only) or nothing
            // to feed; only a `Stdin(source)` + `KeepStdinOpen` run actually waits here. This is what makes
            // the interactive writer and the source feeder single-writer: the feeder drains the source
            // first, then the caller writes.
            host.StdinFeedComplete()
            Some(ProcessStdin(stream, host.Config.StdinEncoding, stdinTarget))
        | None -> None

    /// The non-blocking form of `TakeStdin`: the once-only claim above still happens SYNCHRONOUSLY, before
    /// this returns (so a racing claimant — another `TakeStdin`, or a terminal verb ending an untaken
    /// writer's input — loses, and a caller that gets a task is genuinely the owner), but the wait for a
    /// `Command.Stdin(source)` feeder to finish draining moves into the returned task — served on the
    /// thread pool, where parking a thread is safe, instead of on the caller's. It answers `None` in
    /// exactly the cases `TakeStdin` does, including a writer a completion verb has already claimed.
    ///
    /// Internal, for `ContentLengthSession`: its constructor claims stdin right after starting the framed
    /// parse loop, and must return while the frames that loop is already producing are still unread. With a
    /// bounded frame backlog (`Command.StreamBuffer`) a blocking claim there deadlocks the run — the parse
    /// loop parks on a full channel whose only consumer is `FramesAsync()`, which the caller cannot reach
    /// until the constructor returns; the child then blocks writing stdout, stops reading stdin, and the
    /// very feeder this waits for never finishes.
    member internal _.TakeStdinAsync() : Task<ProcessStdin option> =
        match gate.TryClaimInteractiveStdin() with
        | Some stream ->
            task {
                // The same blocking `host.StdinFeedComplete()` `TakeStdin` performs (it has no async form),
                // moved onto the pool so awaiting it neither blocks the caller's thread nor needs the
                // caller's `SynchronizationContext` to pump — the feeder itself already runs detached
                // there (`Pump.feedStdin`'s `backgroundTask`), so it makes progress regardless.
                do! Task.Run(fun () -> host.StdinFeedComplete())
                return Some(ProcessStdin(stream, host.Config.StdinEncoding, stdinTarget))
            }
        | None -> Task.FromResult None

    /// End the child's input for a `Command.KeepStdinOpen` writer this run's caller never took — the same
    /// once-only claim `TakeStdin` makes, resolved in favour of whichever arrives first, followed by the
    /// platform's own end-of-input delivery on a detached task (see `finishUnclaimedStdin`). A no-op when
    /// stdin was not kept open or the caller already owns the writer.
    ///
    /// Internal, for the terminal verbs that live OUTSIDE this type and never hand the `RunningProcess` to
    /// the caller — `Runner.firstLine`, which starts stdout streaming on a handle nobody else can reach, so
    /// a child needing EOF before its first line would never produce one. Every terminal verb ON this type
    /// calls the helper directly.
    member internal _.FinishUnclaimedStdin() : unit = finishUnclaimedStdin ()

    /// Signal the process tree to die without waiting (fire-and-forget, like `Process.Kill()`); the
    /// tree is fully reaped when the handle is disposed. For a blocking kill, dispose the handle.
    ///
    /// Delivering the kill also starts this handle's bounded post-kill reap window: a tree that cannot
    /// be reaped afterwards (a child wedged in uninterruptible sleep defers even SIGKILL) resolves this
    /// handle's exit wait to an honest `Outcome.Unobserved` once the window elapses, instead of leaving
    /// a caller that killed and then awaited — notably a CANCELLED run, whose token registration calls
    /// exactly this — blocked forever. The native wait is not abandoned: the `PostKillReap` ledger owns
    /// it as the single eventual reaper.
    member _.Kill() =
        host.StartKill()
        terminal.ArmPostKillReap()

    /// Forward parent termination requests into this run's graceful tree-stop path. POSIX registers
    /// `SIGINT` and `SIGTERM`; Windows handles Ctrl+C and Ctrl+Break through `Console.CancelKeyPress`.
    /// The first signal starts one `StopAsync(gracePeriod)` and suppresses the parent's default immediate
    /// termination while the tree stops; repeated signals never start duplicate teardown.
    ///
    /// The returned caller-owned scope removes the handlers when disposed. It is also removed
    /// automatically when the child exits. Registering the scope starts only the handle's shared exit
    /// observation and does not claim stdout/stderr, so capture and streaming verbs remain available.
    /// On Windows the forwarded request uses the ordinary `StopAsync` contract (best-effort `WM_CLOSE`,
    /// then Job termination after the grace window), not a promise that a console child receives the
    /// original Ctrl event.
    member this.ForwardParentSignals(gracePeriod: TimeSpan) : IDisposable =
        ArgumentOutOfRangeException.ThrowIfLessThan(gracePeriod, TimeSpan.Zero, nameof gracePeriod)

        let subscribe (forward: unit -> bool) =
            if RuntimeInformation.IsOSPlatform OSPlatform.Windows then
                let handler =
                    ConsoleCancelEventHandler(fun _ eventArgs ->
                        if forward () then
                            eventArgs.Cancel <- true)

                Console.CancelKeyPress.AddHandler handler

                { new IDisposable with
                    member _.Dispose() =
                        Console.CancelKeyPress.RemoveHandler handler }
            else
                let callback (context: PosixSignalContext) =
                    if forward () then
                        context.Cancel <- true

                let interrupt = PosixSignalRegistration.Create(PosixSignal.SIGINT, callback)

                try
                    let terminate = PosixSignalRegistration.Create(PosixSignal.SIGTERM, callback)

                    { new IDisposable with
                        member _.Dispose() =
                            terminate.Dispose()
                            interrupt.Dispose() }
                with _ ->
                    interrupt.Dispose()
                    reraise ()

        this.ForwardParentSignalsUsing(gracePeriod, subscribe)

    /// `ForwardParentSignals` using the default 2-second graceful-stop window.
    member this.ForwardParentSignals() : IDisposable =
        this.ForwardParentSignals Limits.DefaultStopGrace

    /// Test seam for the forwarding lifecycle: production supplies platform signal registrations;
    /// tests inject a callback holder without sending a real signal to the test runner process.
    member internal this.ForwardParentSignalsUsing
        (gracePeriod: TimeSpan, subscribe: ((unit -> bool) -> IDisposable))
        : IDisposable =
        ArgumentOutOfRangeException.ThrowIfLessThan(gracePeriod, TimeSpan.Zero, nameof gracePeriod)
        ArgumentNullException.ThrowIfNull subscribe

        let registrationGate = obj ()
        let mutable registration: IDisposable option = None
        let mutable disposed = 0
        let mutable forwarded = 0

        let dispose () =
            if Interlocked.Exchange(&disposed, 1) = 0 then
                lock registrationGate (fun () ->
                    registration |> Option.iter (fun value -> value.Dispose())
                    registration <- None)

        let forward () =
            if Volatile.Read(&forwarded) <> 0 then
                // A repeat signal that was already entering the callback when exit auto-unsubscribed
                // still belongs to this forwarding attempt and must suppress the parent's default action.
                true
            elif Volatile.Read(&disposed) <> 0 then
                false
            else
                if Interlocked.CompareExchange(&forwarded, 1, 0) = 0 then
                    let stopTask = this.StopAsync gracePeriod

                    stopTask.ContinueWith(
                        Action<Task<Outcome>>(fun completed ->
                            if completed.IsFaulted then
                                completed.Exception |> ignore),
                        CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default
                    )
                    |> ignore

                true

        let created = subscribe forward

        lock registrationGate (fun () ->
            if Volatile.Read(&disposed) <> 0 then
                created.Dispose()
            else
                registration <- Some created)

        let exitTask = ensureBufferedWait ()

        exitTask.ContinueWith(
            Action<Task<Outcome>>(fun _ -> dispose ()),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default
        )
        |> ignore

        { new IDisposable with
            member _.Dispose() = dispose () }

    /// Resize the child's controlling pseudo-terminal to `cols` columns x `rows` rows (a `Command.Pty`
    /// run only). Windows applies it with `ResizePseudoConsole`; POSIX applies `ioctl(TIOCSWINSZ)` on the
    /// pty master and then delivers `SIGWINCH` to the child so a running TUI re-queries its geometry (D6).
    ///
    /// Honest, never a silent no-op: on a **non-PTY** run this returns `Error(ProcessError.Unsupported)`,
    /// and a native resize failure returns `Error(ProcessError.Io ...)` — a garbled/partial resize is
    /// never reported as success. `cols` and `rows` must each be at least 1 and at most `Int16.MaxValue`
    /// (a terminal `COORD`/`winsize` is a `SHORT`), rejected with `ArgumentOutOfRangeException` at the
    /// boundary, matching the `Command.Pty` builder's geometry validation.
    ///
    /// A **pure**, non-consuming verb: it neither consumes the output pipes nor touches the exit-wait/reap
    /// path, so it never trips the "already consumed by another verb" gate and can run alongside a
    /// capturing/streaming/`WaitAsync` verb that has claimed the handle. It is honest about lifecycle,
    /// though: once the run has been **torn down** — a terminal verb has concluded and reaped it, or the
    /// handle has been disposed — the pty master fd / pseudoconsole handle behind the resize is closed, and
    /// its number is reusable by another run, so a resize then returns `Error(ProcessError.Unsupported ...)`
    /// rather than risk `ioctl`/`SIGWINCH`/`ResizePseudoConsole` landing on an unrelated run through a
    /// recycled fd/pid/handle. Resize a run while it is live.
    member _.ResizeAsync(cols: int, rows: int) : Task<Result<unit, ProcessError>> =
        ArgumentOutOfRangeException.ThrowIfLessThan(cols, 1, nameof cols)
        ArgumentOutOfRangeException.ThrowIfGreaterThan(cols, int Int16.MaxValue, nameof cols)
        ArgumentOutOfRangeException.ThrowIfLessThan(rows, 1, nameof rows)
        ArgumentOutOfRangeException.ThrowIfGreaterThan(rows, int Int16.MaxValue, nameof rows)

        // No claim gate, no consumption, no `host.Wait()`/`ensureBufferedWait()` — a resize is
        // independent of the exit-wait/reap-once ledger (KB K-016) and is not a consuming verb (KB K-031).
        match host.ResizePty with
        | Some resize -> Task.FromResult(resize (cols, rows))
        | None -> Task.FromResult(Error(ProcessError.Unsupported "Resize (not a PTY run)"))

    /// Gracefully stop the process tree, then reap it: send the command's configured `StopSignal`, wait
    /// up to `gracePeriod` for it to exit on its own, then hard-kill whatever is still alive — the
    /// same graceful-kill machinery `Command.TimeoutGrace` and `ProcessGroup.ShutdownAsync` drive.
    /// Returns the honest `Outcome` of how the child *actually* concluded (a clean `Exited` if it
    /// obeyed the signal, otherwise a `Signalled`/`Exited` from the escalated kill); a non-zero or
    /// killed exit is data, never a raised error. Unlike the fire-and-forget `Kill()`, this awaits the
    /// stop and tears the tree down before returning, so it is a terminal verb like `WaitAsync`.
    /// A negative `gracePeriod` is rejected with `ArgumentOutOfRangeException`; `TimeSpan.Zero`
    /// skips the grace window and escalates immediately.
    ///
    /// **Bounded, always.** The whole call — the grace window and the reap that follows the escalated
    /// hard kill — is bounded by `gracePeriod` plus a post-kill reap budget. A tree that cannot be
    /// reaped even after the hard kill lands (a child wedged in uninterruptible sleep defers SIGKILL
    /// until its I/O unblocks) therefore ends this call with an honest `Outcome.Unobserved` carrying
    /// that detail, never a fabricated exit and never an unbounded block. The wait is not dropped: the
    /// single remaining right to reap the tree passes to a background reaper, so nothing starts a
    /// second waiter and the eventual conclusion is still observed exactly once.
    ///
    /// This drains the child's stdout/stderr while it shuts down (a child blocked writing to a full
    /// pipe would otherwise ignore the soft signal until it could flush). If a streaming or capturing
    /// verb already owns the pipes, `StopAsync` reuses that session's wait rather than starting a
    /// second reader on them, so it is safe to call after `StdoutLinesAsync`/`OutputEventsAsync` or
    /// concurrently with an in-flight `FinishAsync`/`WaitAsync`. Idempotent and race-safe with `Kill`,
    /// `Dispose`, and a repeat `StopAsync`: the tree is reaped exactly once.
    ///
    /// **Platform / shared-group degradation (no new silent downgrade).** A soft signal needs a
    /// mechanism that has one. On **Windows** there is no per-tree graceful signal, but a windowed child
    /// (Electron/GUI) is sent a best-effort `WM_CLOSE` at the start of the grace window and can close
    /// itself within it; a child with no window (or one that vetoes the close) is hard-killed by the
    /// atomic Job terminate when the grace elapses — exactly as `Command.TimeoutGrace` and
    /// `ProcessGroup.ShutdownAsync` behave there (a console child can additionally get a best-effort
    /// CTRL+BREAK via `Command.WindowsCtrlSignals()` + `ProcessGroup.Signal`). On a **shared** group
    /// (a handle from `ProcessGroup.StartAsync`, where the group — not the handle — owns the tree)
    /// there is no per-child graceful signal either, so this immediately hard-kills just this child
    /// (like `Kill()`), matching the documented `TimeoutGrace` fallback for a shared group. A handle
    /// from the default runner (`Command.StartAsync()` / `IProcessRunner.SpawnAsync`) owns a private
    /// group and gets the full configured-soft-signal → grace → SIGKILL path on Unix.
    member this.StopAsync(gracePeriod: TimeSpan) : Task<Outcome> =
        ArgumentOutOfRangeException.ThrowIfLessThan(gracePeriod, TimeSpan.Zero)

        task {
            use _reap = terminal.ReapGuard()
            // Release any bounded writer before asking the shared exit task to settle. This is the
            // terminal operation's explicit signal that an unread streaming backlog may be abandoned;
            // keep the disposal token untouched so the pump's normal I/O classification remains intact.
            terminal.CancelBackpressureWriters()
            // Begin (or reuse) the exit wait BEFORE signalling, so the pipes are drained while the
            // child shuts down. `ExitTask` reuses whichever consumption already owns the pipes (a
            // streaming session, or an in-flight buffered verb) rather than racing a second reader,
            // and claims a fresh buffered drain only when no verb has run yet. It never reaps.
            let exitTask = this.ExitTask
            // Start racing the shared conclusion BEFORE the stop is asked for, bounded by this call's
            // own window: the grace it was given, plus the post-kill reap budget. Both halves of the
            // stop live inside that one window — the graceful wait (up to `gracePeriod`) and the reap
            // that follows the escalated hard kill — so a repeat `StopAsync` (which skips the kill and
            // only awaits the shared outcome) is bounded by exactly the same rule as the caller that
            // performs the escalation, without either of them cutting a still-legitimate grace window
            // short. Mirrors the ProcessKit-rs prototype's `grace.saturating_add(PUMP_TEARDOWN)`.
            let bounded =
                PostKillReap.awaitWithin (PostKillReap.plus gracePeriod (PostKillReap.budget ())) exitTask

            // Ask the tree to stop: soft signal, wait up to `gracePeriod`, then hard-kill the remainder
            // — reusing `host.GracefulKill`, the timeout machinery's own escalation. Degrades to the
            // documented immediate child/tree kill on Windows or a shared group (see the doc above).
            // Fired at most once (a repeat `StopAsync` only awaits the outcome), so it never re-enters
            // the native kill on a container a prior stop/`Dispose` already released.
            if terminal.TryBeginStop() then
                do! host.GracefulKill gracePeriod
                // The escalation has now delivered the hard kill (`GracefulKill` returns only after its
                // grace-bounded poll force-killed whatever was still alive), so this is the honest
                // moment to start the handle's post-kill reap window — for this call and for any other
                // verb sharing the same exit wait.
                terminal.ArmPostKillReap()

            match! bounded with
            | ValueSome outcome ->
                // Record the run as completed (once-guarded: a no-op if a concurrent terminal verb
                // sharing the same wait already concluded it). Return the honest outcome; a killed/
                // non-zero exit is data, so this never raises for the stop itself.
                conclude outcome
                return outcome
            | ValueNone ->
                // The tree was asked to stop and then hard-killed, but its conclusion did not land
                // inside the window. Report that honestly rather than blocking indefinitely or
                // fabricating an exit: the shared wait is now owned by the `PostKillReap` ledger (no
                // second waiter, its eventual fault observed), and a verb still awaiting the same
                // `ExitTask` will still see the real outcome if it ever arrives.
                let outcome = Outcome.Unobserved RunTerminal.StopUnobservedReason
                conclude outcome
                return outcome
        }

    /// `StopAsync` using the default 2-second grace window (matching `ProcessGroupOptions.ShutdownTimeout`).
    member this.StopAsync() : Task<Outcome> = this.StopAsync Limits.DefaultStopGrace

    /// Deliver `signal` to this run's own contained process tree without consuming or reaping the
    /// handle. Delivery is lifecycle-gated: after teardown it returns a typed `Unsupported` error and
    /// never targets a recycled pid. On Windows only the documented Job/CTRL+BREAK/WM_CLOSE mappings are
    /// available; unsupported signals fail honestly.
    member _.Signal(signal: Signal) : Result<unit, ProcessError> = host.Signal signal

    /// Run to completion, capturing stdout as decoded text. A non-zero exit is data; the tree is
    /// reaped when the call returns.
    member _.OutputStringAsync() : Task<Result<ProcessResult<string>, ProcessError>> =
        if not (gate.TryClaimBuffered()) then
            Task.FromResult(Error(alreadyConsumedError ()))
        else

            task {
                use _reap = terminal.ReapGuard()
                // The captures are owned HERE, not by the pumps: a pump the bounded post-exit output
                // drain could not end (an inherited pipe a descendant still holds open) is abandoned
                // rather than awaited, and this verb must still report what it captured.
                let outBuf = sessions.NewCaptureBuffer()
                let errBuf = sessions.NewCaptureBuffer()
                let stdoutTask = sessions.PumpStdoutBuffer outBuf
                let stderrTask = sessions.PumpStderrBuffer errBuf
                // This verb runs the child to completion, so no caller can write its stdin past here: end
                // the input of a `KeepStdinOpen` writer nobody took, or a child reading to EOF never exits.
                // A no-op when there is none (see `finishUnclaimedStdin`). After the pumps, so the drains
                // that keep the child moving are already in place when its input ends.
                finishUnclaimedStdin ()
                // Observe BOTH buffer pumps before reading either capture, so a throwing line handler in
                // one never orphans the other as an unobserved task (mirrors the streaming path's
                // WhenAll); `RunTerminal.AwaitBufferedOutcome` additionally guarantees this even if the exit wait
                // itself faults, and bounds the join so a held-open inherited pipe can't hang this verb.
                let! outcome = terminal.AwaitBufferedOutcome(ensureBufferedWait (), [| stdoutTask; stderrTask |])

                conclude outcome

                // The volume both captures SAW — retained plus dropped — saturating at `Int32.MaxValue`
                // like the buffers' own counters. One pair, two consumers: the fail-loud error below,
                // and (carried on a successful result) the truncation refusal a checking verb makes
                // later, so both report the same honest totals. The line count is always available;
                // the byte count is available only when the configured policy made the line pumps scan
                // UTF-8 byte sizes.
                let totalLines =
                    int (min (int64 outBuf.TotalLines + int64 errBuf.TotalLines) (int64 Int32.MaxValue))

                let totalBytes =
                    int (min (int64 outBuf.TotalBytes + int64 errBuf.TotalBytes) (int64 Int32.MaxValue))

                if outBuf.TooLarge || errBuf.TooLarge then
                    return Error(tooLargeError totalLines totalBytes)
                else
                    match! stdinErrorOnSuccess outcome with
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
                                    recordedTruncated
                                    || outBuf.Truncated
                                    || errBuf.Truncated
                                    // A capture the post-exit drain bound cut short is incomplete, and
                                    // says so — never a partial capture reported as the whole output.
                                    || terminal.OutputDrainWasBounded,
                                    config.OkCodes,
                                    ?configuredTimeoutDuration = terminal.ConfiguredTimeoutDuration,
                                    stdoutEncoding = config.StdoutEncoding,
                                    overflowTotals =
                                        (Some totalLines,
                                         if
                                             config.OutputBuffer.MaxBytes.IsSome
                                             || config.OutputBuffer.Overflow = OverflowMode.Error
                                         then
                                             Some totalBytes
                                         else
                                             None),
                                    // WHICH of the two truncation sources this was, so a checking verb
                                    // refusing the capture names the real cause instead of quoting a
                                    // ceiling that was never configured (`rejectIfTruncated`).
                                    outputDrainBounded = terminal.OutputDrainWasBounded
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
        if not (gate.TryClaimBuffered()) then
            Task.FromResult(Error(alreadyConsumedError ()))
        else

            task {
                use _reap = terminal.ReapGuard()

                // The raw stdout capture now honours the byte cap + overflow of `config.OutputBuffer`
                // (unbounded when `MaxBytes = None`, exactly as before); `MaxLines` does not apply to a
                // byte stream, so it is ignored here — it still governs the line-pumped stderr below. Both
                // captures are owned HERE (see the text verb above): a concurrent `StopAsync`/`Dispose`
                // teardown race, and a pump the post-exit drain bound had to abandon, both leave this verb
                // holding the bytes that did arrive rather than nothing — T-087.
                let stdoutSink = Pump.RawSink config.OutputBuffer
                let errBuf = sessions.NewCaptureBuffer()
                let stdoutTask = sessions.CaptureRawStdout stdoutSink
                let stderrTask = sessions.PumpStderrBuffer errBuf
                // As on the text verb above: nobody can write this child's stdin past a completion verb, so
                // an untaken `KeepStdinOpen` writer's input ends here (a no-op when there is none).
                finishUnclaimedStdin ()
                // Observe both pumps before reading either capture, so a throwing stderr handler (or a
                // raw-drain I/O fault) can't orphan the other as an unobserved task; `AwaitBufferedOutcome`
                // additionally guarantees this even if the exit wait itself faults, and bounds the join.
                let! outcome = terminal.AwaitBufferedOutcome(ensureBufferedWait (), [| stdoutTask; stderrTask |])

                let stdoutCapture = stdoutSink.Snapshot()
                conclude outcome

                // The raw stdout byte cap contributes no lines (a byte stream has none); stderr is
                // line-pumped, so its totals carry the lines and both streams' bytes are summed. As on
                // the text verb above, this one pair serves both the fail-loud error and the totals a
                // successful result carries for a later truncation refusal. The stderr line count is
                // always available; the combined byte count is available only when stderr bytes were
                // counted as well as raw stdout bytes.
                let totalLines = errBuf.TotalLines

                let totalBytes =
                    int (min (int64 stdoutCapture.TotalBytes + int64 errBuf.TotalBytes) (int64 Int32.MaxValue))

                if stdoutCapture.TooLarge || errBuf.TooLarge then
                    return Error(tooLargeError totalLines totalBytes)
                else
                    match! stdinErrorOnSuccess outcome with
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
                                    recordedTruncated
                                    || stdoutCapture.Truncated
                                    || errBuf.Truncated
                                    // Symmetric with the text verb: a capture the post-exit drain bound
                                    // cut short reports itself as incomplete.
                                    || terminal.OutputDrainWasBounded,
                                    config.OkCodes,
                                    ?configuredTimeoutDuration = terminal.ConfiguredTimeoutDuration,
                                    stdoutEncoding = config.StdoutEncoding,
                                    overflowTotals =
                                        (Some totalLines,
                                         if
                                             config.OutputBuffer.MaxBytes.IsSome
                                             || config.OutputBuffer.Overflow = OverflowMode.Error
                                         then
                                             Some totalBytes
                                         else
                                             None),
                                    // Symmetric with the text verb here too: the refusal a checking verb
                                    // builds from this result must name the drain bound, not a ceiling.
                                    outputDrainBounded = terminal.OutputDrainWasBounded
                                )
                            )
            }

    /// Wait for the process to exit, discarding its output. Reaps the tree.
    member _.WaitAsync() : Task<Outcome> =
        if not (gate.TryClaimBuffered()) then
            raise (InvalidOperationException alreadyConsumedMessage)

        task {
            use _reap = terminal.ReapGuard()
            // Drain both pipes (so the child never blocks on a full buffer) without retaining.
            let stdoutTask = sessions.DrainStdoutDiscarding()
            let stderrTask = sessions.DrainStderrDiscarding()
            // As on the capture verbs above: nobody can write this child's stdin past a completion verb, so
            // an untaken `KeepStdinOpen` writer's input ends here (a no-op when there is none).
            finishUnclaimedStdin ()
            // Observe both drains together so an I/O fault on one can't orphan the other;
            // `RunTerminal.AwaitBufferedOutcome` also guarantees this even if the exit wait itself faults.
            let! outcome = terminal.AwaitBufferedOutcome(ensureBufferedWait (), [| stdoutTask; stderrTask |])
            conclude outcome
            return outcome
        }

    /// Run to completion while periodically sampling the child's CPU/memory and, where available, its
    /// private containment tree's I/O every `interval`, then return a `RunProfile`. Drains and discards
    /// output (like `WaitAsync`) and reaps the tree. A run in a shared group reports no I/O counters,
    /// because the group's aggregate would include sibling runs.
    /// A non-positive `interval` (`<= TimeSpan.Zero`) is rejected with `ArgumentOutOfRangeException`
    /// — a sampling cadence must be a positive duration. Validated up front, before the pipes are
    /// claimed, so an invalid call neither consumes this one-shot handle nor starts a tight loop.
    member _.ProfileAsync(interval: TimeSpan) : Task<RunProfile> =
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero)

        if not (gate.TryClaimBuffered()) then
            raise (InvalidOperationException alreadyConsumedMessage)

        task {
            use _reap = terminal.ReapGuard()

            let period = interval

            let mutable samples = 0
            let mutable lastCpu = None
            let mutable peakMemory = None
            let mutable lastIo = None
            use sampleCts = new CancellationTokenSource()

            let sampleTreeIo () =
                match host.TreeStats with
                | Some stats ->
                    match stats () with
                    | Some snapshot -> snapshot.IoCounters |> Option.iter (fun counters -> lastIo <- Some counters)
                    | None -> ()
                | None -> ()

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

                            sampleTreeIo ()

                            samples <- samples + 1
                            // Clamp so an over-long sampling period can't throw out of `Task.Delay`.
                            do! Task.Delay(Timeouts.clampArmable period, sampleCts.Token)
                    with :? OperationCanceledException ->
                        // The run finished and we cancelled sampling; stop quietly.
                        ()
                }

            let stdoutTask = sessions.DrainStdoutDiscarding()
            let stderrTask = sessions.DrainStderrDiscarding()

            // As on the capture verbs above: nobody can write this child's stdin past a completion verb, so
            // an untaken `KeepStdinOpen` writer's input ends here (a no-op when there is none).
            finishUnclaimedStdin ()

            // Capture a fault rather than letting it escape immediately, so the sampler is ALWAYS
            // cancelled and awaited before its CTS is disposed at scope exit — never left running as
            // an unobserved task. (A task CE cannot `do!` inside a `finally`, so this is the
            // try/with-then-single-cleanup form of try/finally; the cleanup must also precede reading
            // the sampler's metrics on the success path, which a `finally`/`use` could not guarantee.)
            let mutable error: exn option = None
            let mutable outcome = Unchecked.defaultof<Outcome>

            try
                let! settled = ensureBufferedWait ()
                // Bounded exactly like every other post-exit pump join on this handle: a descendant that
                // inherited the child's stdout/stderr must not be able to hold this profile open past
                // the leader's own conclusion (see `awaitPumpsSettled`).
                do! terminal.DrainPumpsBounded [| stdoutTask; stderrTask |]
                outcome <- settled
            with ex ->
                error <- Some ex
                // A fault before the drains were awaited (e.g. the timeout race threw) must not orphan
                // them — observe them best-effort. Their own fault is secondary to the error we surface.
                try
                    do! terminal.DrainPumpsBounded [| stdoutTask; stderrTask |]
                with _ ->
                    // best-effort teardown drain; the original fault above is what we report.
                    ()

            sampleCts.Cancel()
            do! sampler

            match error with
            | Some ex -> return! Task.FromException<RunProfile> ex
            | None ->
                // Job/cgroup accounting remains queryable after the child exits and is cumulative, so
                // take one final snapshot before this private group is released. It closes the common
                // short-run race where the child finishes between periodic ticks.
                sampleTreeIo ()
                conclude outcome
                return RunProfile(outcome, elapsed (), lastCpu, peakMemory, lastIo, samples)
        }

    /// `ProfileAsync` sampling every 100 ms.
    member this.ProfileAsync() =
        this.ProfileAsync(TimeSpan.FromMilliseconds 100.0)

    // Claim (or rejoin) the stdout LINE-streaming session: the gate decides, `OutputSessions` builds,
    // and both happen under the gate's ONE lock (`ConsumptionGate.TryClaimStdoutStreaming`) — so a
    // concurrent second `StdoutLinesAsync`/`WaitForLineAsync`/`FinishAsync` either observes a
    // fully-constructed session (channel + pumps + outcome all assigned) or, if it is an incompatible
    // consumer, is atomically refused; never a half-built session, and never two racing setups building
    // two readers on the one channel.
    //
    // Returns false when a different consumption (a buffered verb, or event streaming) already owns the
    // pipes, and — for a non-terminal caller — once a terminal `FinishAsync` has discarded this session's
    // stdout; true once the stdout streaming session is (or already was) ours.
    //
    // `terminalOnly` marks the ONE caller that ends the run rather than consuming its stdout —
    // `FinishAsync`. Reaching the gate from there with no enumerator handed out makes the session a
    // retain-nothing drain instead of a queue nobody will empty, AND closes the gate to every later
    // non-terminal stdout-line caller — the two halves latched together, under that one lock, before the
    // pumps are built (KB K-163).
    member private _.StartStdoutStreaming(terminalOnly: bool) : bool =
        gate.TryClaimStdoutStreaming(terminalOnly, (fun () -> sessions.StartLineSession()))

    // The byte-chunk counterpart to `StartStdoutStreaming`. It owns the same stdout pipe and stderr
    // capture, but its pump deliberately does no decoding or line framing: one channel item is one
    // non-empty OS read. The claim is reentrant for `FinishAsync`/`ExitTask`, while the public
    // `StdoutChunksAsync` below keeps its own one-enumerator guard — and it needs no `terminalOnly`
    // discard counterpart (see `ConsumptionGate.TryClaimStdoutChunkStreaming` for why).
    member private _.StartStdoutChunkStreaming() : bool =
        gate.TryClaimStdoutChunkStreaming(fun () -> sessions.StartChunkSession())

    /// Stream stdout as raw byte chunks. Each item is a non-empty `ReadOnlyMemory<byte>` containing
    /// exactly one underlying read, including NUL bytes, invalid UTF-8, and arbitrary read boundaries.
    /// The returned memory owns its backing array and remains valid after the next item is produced.
    /// Call `FinishAsync()` afterwards for stderr and the process outcome.
    member this.StdoutChunksAsync() : IAsyncEnumerable<ReadOnlyMemory<byte>> =
        if not (this.StartStdoutChunkStreaming()) then
            raise (InvalidOperationException alreadyConsumedMessage)

        if not (gate.TryTakeStdoutChunksEnumerator()) then
            raise (InvalidOperationException alreadyConsumedMessage)

        sessions.StdoutChunks

    // The stderr counterpart to `StartStdoutChunkStreaming`: the same claim shape with the two pipes'
    // roles swapped — stderr is pumped raw for the caller, stdout is pumped and retained nowhere (see
    // `OutputSessions.StartStderrChunkSession`). Reentrant for `FinishAsync`/`ExitTask`, while the
    // public `StderrChunksAsync` below keeps its own one-enumerator guard, and it needs no
    // `terminalOnly` discard counterpart either (see `ConsumptionGate.TryClaimStderrChunkStreaming`,
    // which spells out the K-163 audit for both halves of this session).
    member private _.StartStderrChunkStreaming() : bool =
        gate.TryClaimStderrChunkStreaming(fun () -> sessions.StartStderrChunkSession())

    /// Stream **stderr** as raw byte chunks — the exact counterpart of `StdoutChunksAsync`, for
    /// diagnostics that text is the wrong abstraction for (a binary progress protocol, a high-volume
    /// log a caller wants to relay or hash byte-for-byte). Each item is a non-empty
    /// `ReadOnlyMemory&lt;byte&gt;` containing exactly one underlying read, including NUL bytes,
    /// invalid UTF-8, and arbitrary read boundaries; the returned memory owns its backing array and
    /// remains valid after the next item is produced. Same `Command.StreamBuffer` backpressure/drop/
    /// fail-loud policy, same `StderrTee` raw tee, same teardown behaviour as the stdout chunk stream,
    /// and the same one-shot contract: a second call — or any other consuming verb — is refused with
    /// the already-consumed `InvalidOperationException`. Call `FinishAsync()` afterwards for the
    /// process outcome.
    ///
    /// **This run's stdout is drained and discarded.** `Finished` carries the outcome and the captured
    /// stderr, which this verb has just handed to the caller as bytes, so there is nothing for a
    /// terminal verb to return stdout through — and retaining it would pin a whole run's output in
    /// memory for nobody (T-357). stdout is still read, framed, teed (`StdoutTee`), handed to
    /// `OnStdoutLine` and counted into `StdoutLineCount`, so the child never blocks on a full pipe; it
    /// is simply never retained, and asking for it afterwards (`StdoutLinesAsync`/`StdoutChunksAsync`/
    /// `OutputStringAsync`/...) is refused rather than answered with an empty stream. Capture stdout
    /// with `Command.StdoutTee`/`StdoutToFile` if you need both.
    ///
    /// **No separate stderr, no fake stream.** A run whose stderr does not reach the parent as its own
    /// pipe cannot have a byte-exact stderr stream at all: `Command.MergeStderr` folds stderr into
    /// stdout at the OS level, a `Command.Pty` run gives the child one terminal device, and
    /// `StderrToFile`/`StdioMode.Inherit`/`StdioMode.Null` leave no parent-side stream. Each of those
    /// raises `ProcessException` carrying `ProcessError.Unsupported` (naming which one it was) instead
    /// of returning an empty enumerable that would read as "the child wrote nothing to stderr" — under
    /// a merge, use `StdoutChunksAsync()`, where those bytes really are. The refusal happens BEFORE the
    /// pipes are claimed, so the handle is left untouched and every other verb remains available.
    member this.StderrChunksAsync() : IAsyncEnumerable<ReadOnlyMemory<byte>> =
        match stderrChunksUnsupported () with
        | Some error -> raise (ProcessException error)
        | None ->
            if not (this.StartStderrChunkStreaming()) then
                raise (InvalidOperationException alreadyConsumedMessage)

            if not (gate.TryTakeStderrChunksEnumerator()) then
                raise (InvalidOperationException alreadyConsumedMessage)

            sessions.StderrChunks

    /// Stream stdout line by line as it arrives. Call `FinishAsync` afterwards for stderr + outcome.
    /// Hands out its ONE enumerator exactly once per handle — a second call (directly, or via
    /// `StdoutJsonLinesAsync`, which itself calls this) throws `InvalidOperationException`, same as any
    /// other already-consumed verb; `FinishAsync`/`WaitForLineAsync` remain free to rejoin the same
    /// session afterwards (they do not produce a second enumerator). Take the stream BEFORE finishing:
    /// a `FinishAsync` that ran with no enumerator handed out discards stdout as it arrives, so this
    /// throws the same already-consumed `InvalidOperationException` afterwards rather than returning a
    /// stream that could only be empty.
    member this.StdoutLinesAsync() : IAsyncEnumerable<string> =
        if not (this.StartStdoutStreaming(terminalOnly = false)) then
            raise (InvalidOperationException alreadyConsumedMessage)

        if not (gate.TryTakeStdoutLinesEnumerator()) then
            // Either this handle's ONE enumerator was already handed out — the claim above is
            // deliberately reentrant, so it alone cannot refuse a second enumerator-producing call — or a
            // `FinishAsync` racing between that claim and this second lock acquisition latched the
            // retain-nothing sink, which the gate re-checks here so the stream this call would return can
            // never quietly run dry. Concurrent verbs on one handle are undefined API-wide, but
            // "undefined" must not mean "silently empty": both cases get the same loud refusal (KB K-163).
            raise (InvalidOperationException alreadyConsumedMessage)

        sessions.StdoutLines

    /// Stream stdout as NDJSON / JSON Lines: each non-empty line is deserialized into a `'T` via
    /// `System.Text.Json` (`options` omitted uses the BCL defaults) as it arrives. A thin wrapper over
    /// `StdoutLinesAsync()` — it shares the very same exclusive-consumption gate
    /// (`StartStdoutStreaming`) and the same already-consumed enumerator guard, `LineTerminator`, and
    /// `StreamBuffer` policy, so calling this instead of `StdoutLinesAsync()` (or vice versa, or twice)
    /// on one handle follows the same already-consumed contract every other streaming verb already has;
    /// nothing extra needs configuring here. An empty line (after that line-terminator policy is applied) is skipped
    /// silently, never deserialized — a common NDJSON producer quirk (a trailing blank line, a
    /// keep-alive newline). A non-empty line that fails to deserialize ends the enumeration with
    /// `ProcessException(ProcessError.Parse(...))`, exactly like every other JSON verb's
    /// `ProcessError.Parse` (`OutputJsonAsync`/`ParseAsync`) — never a raw, undocumented exception
    /// escaping the `IAsyncEnumerable`. Call `FinishAsync()` afterwards for stderr + outcome, same as
    /// after `StdoutLinesAsync()`.
    ///
    /// **Trimming / AOT:** deserializes via reflection-based `System.Text.Json`
    /// (`JsonSerializer.Deserialize(string, Type, JsonSerializerOptions)`), so it is not trim-/AOT-safe
    /// — pass a `JsonTypeInfo&lt;'T&gt;` via the other overload, or avoid this verb, in a
    /// trimmed/NativeAOT app.
    [<RequiresUnreferencedCode "Deserializes each line by reflection via System.Text.Json; give the JsonTypeInfo<'T> overload, or avoid this verb, in a trimmed app.">]
    [<RequiresDynamicCode "Deserializes each line by reflection via System.Text.Json; give the JsonTypeInfo<'T> overload, or avoid this verb, in a NativeAOT app.">]
    member this.StdoutJsonLinesAsync<'T>([<Optional>] options: JsonSerializerOptions | null) : IAsyncEnumerable<'T> =
        // Non-generic, `Type`-based overload rather than the generic `JsonSerializer.Deserialize<'T>` —
        // same reasoning as `CaptureVerbs.outputJson`: the BCL's generic overload returns a
        // `TValue?`-annotated value the F# nullness checker can't reconcile against our ambient,
        // unconstrained `'T`. A genuine JSON `null` raises here, turned into `ProcessError.Parse` below
        // exactly like a malformed document would.
        let optionsArg = Option.ofObj options |> Option.toObj

        let deserialize (line: string) : 'T =
            match JsonSerializer.Deserialize(line, typeof<'T>, optionsArg) with
            | null -> raise (JsonException "the JSON document deserialized to null")
            | value -> unbox<'T> value

        JsonLinesEnumerable<'T>(config.Program, this.StdoutLinesAsync(), deserialize) :> IAsyncEnumerable<'T>

    /// Like the overload above, but deserializes each line via a source-generated
    /// `JsonTypeInfo&lt;'T&gt;` instead of reflection — no `RequiresUnreferencedCode`/
    /// `RequiresDynamicCode`, so this overload is trim-/NativeAOT-safe. Pass
    /// `MyJsonContext.Default.MyType` from a `[&lt;JsonSerializable&gt;]`-annotated
    /// `JsonSerializerContext`. Same empty-line-skip / `ProcessError.Parse` contract as the reflection
    /// overload above.
    member this.StdoutJsonLinesAsync<'T>(typeInfo: JsonTypeInfo<'T>) : IAsyncEnumerable<'T> =
        ArgumentNullException.ThrowIfNull typeInfo

        // Through the non-generic `JsonTypeInfo` base overload for the same reason the reflection
        // overload above goes through `typeof<'T>` rather than the generic `Deserialize<'T>` — sidesteps
        // the BCL's `TValue?`-annotated generic return the F# nullness checker can't reconcile against
        // an unconstrained `'T`.
        let deserialize (line: string) : 'T =
            match JsonSerializer.Deserialize(line, typeInfo :> JsonTypeInfo) with
            | null -> raise (JsonException "the JSON document deserialized to null")
            | value -> unbox<'T> value

        JsonLinesEnumerable<'T>(config.Program, this.StdoutLinesAsync(), deserialize) :> IAsyncEnumerable<'T>

    /// After streaming stdout, wait for exit and return the captured stderr. Reaps the tree.
    ///
    /// Safe to call without streaming first: stdout is then drained to keep the child moving and
    /// **discarded as it arrives**, retaining nothing — `Finished` carries the outcome and stderr, never
    /// stdout. Asking for that stdout afterwards is refused, not answered with an empty stream: a
    /// later `StdoutLinesAsync`/`StdoutJsonLinesAsync` throws `InvalidOperationException` and a later
    /// `WaitForLineAsync` returns `ProcessError.Unsupported`, the same already-consumed answer they give
    /// after `WaitAsync`/`ProfileAsync`. Capture stdout with `OutputStringAsync`/`OutputBytesAsync`, or
    /// take `StdoutLinesAsync`/`StdoutChunksAsync` BEFORE finishing, if you need it.
    /// A stream that WAS handed out keeps the existing hand-off semantics unchanged: everything the
    /// child wrote stays queued for its enumerator, dropped or bounded only by the `StreamBuffer`
    /// policy the caller opted into.
    ///
    /// After `StderrChunksAsync()` this is still the terminal hand-off, with `Finished.Stderr` empty by
    /// construction: that session hands the stderr BYTES to the caller instead of capturing them, so
    /// there is no text capture to return (see `StderrChunksAsync`).
    member this.FinishAsync() : Task<Result<Finished, ProcessError>> =
        let outcomeTask =
            // `terminalOnly`: this verb ends the run, it does not consume stdout. With no enumerator
            // handed out, the session's stdout sink becomes a retain-nothing drain instead of a queue
            // nobody can read (see `ConsumptionGate`'s discard latch) — for the fresh handle this call
            // starts the session for, and for a `WaitForLineAsync`-started one nobody took over either.
            if this.StartStdoutStreaming(terminalOnly = true) then
                Some sessions.LineOutcome
            elif this.StartStdoutChunkStreaming() then
                Some sessions.ChunkOutcome
            elif this.StartStderrChunkStreaming() then
                // Only ever reached for a handle a `StderrChunksAsync` consumer already claimed (the
                // two claims above win on a fresh one), so this is the terminal hand-off after that
                // stream — never a session this call starts. `Finished.Stderr` is then empty by
                // construction: those bytes are the caller's, not a capture to hand back.
                Some sessions.StderrChunkOutcome
            else
                None

        match outcomeTask with
        | None -> Task.FromResult(Error(alreadyConsumedError ()))
        | Some outcome ->
            // `FinishAsync` is the explicit terminal hand-off after a streaming consumer has stopped.
            // Wake any bounded writer before awaiting the shared session outcome; otherwise an unread
            // full channel would keep this very task waiting for the pump that is waiting for a reader.
            terminal.CancelBackpressureWriters()

            task {
                use _reap = terminal.ReapGuard()
                let! settled = outcome
                conclude settled

                if sessions.SessionStderrTooLarge then
                    return Error(tooLargeError sessions.SessionStderrTotalLines sessions.SessionStderrTotalBytes)
                else
                    match! stdinErrorOnSuccess settled with
                    | Some err -> return Error err
                    | None ->
                        return
                            Ok(
                                Finished(
                                    settled,
                                    sessions.SessionStderrText,
                                    recordedTruncated
                                    || sessions.AnyStreamLinesDropped
                                    || sessions.SessionStderrTruncated
                                    // Symmetric with the buffered capture verbs: output this session's
                                    // post-exit drain bound cut short is reported as incomplete, whether
                                    // it was the captured stderr or the stdout a consumer streamed.
                                    || terminal.OutputDrainWasBounded
                                )
                            )
            }

    // Claim the merged output-event session — the gate decides, `OutputSessions` builds, both under
    // the gate's one lock, so a concurrent second `OutputEventsAsync` observes a fully-constructed
    // session or is atomically refused. Returns false when a different consumption (a buffered verb, or
    // stdout streaming) already owns the pipes, OR when the event session itself was already claimed by
    // an earlier `OutputEventsAsync()` call; true only for the ONE call that first claims it.
    //
    // Needs no `terminalOnly` discard counterpart either (T-357): `OutputEventsAsync()` — the verb that
    // hands out the enumerator — is this session's ONLY entry point, so an event channel can never be
    // filled for a consumer that does not exist. No terminal verb starts or rejoins it: `FinishAsync`
    // goes through the stdout claims above (and is refused outright once `EventStreaming` owns the pipes),
    // and `ExitTask`/`StopAsync` reuse the session's outcome directly without re-entering here.
    member private _.StartEventStreaming() : bool =
        gate.TryClaimEventStreaming(fun () -> sessions.StartEventSession())

    /// Stream merged stdout+stderr line events as they arrive, each tagged with its origin
    /// (`OutputEvent.Stdout`/`OutputEvent.Stderr`). Under `Command.MergeStderr` the child has no separate
    /// stderr stream (it is folded into stdout at the OS level), so every event is an `OutputEvent.Stdout`
    /// — the stderr lines are already interleaved, in order, within the stdout byte stream.
    member this.OutputEventsAsync() : IAsyncEnumerable<OutputEvent> =
        if not (this.StartEventStreaming()) then
            raise (InvalidOperationException alreadyConsumedMessage)

        sessions.OutputEvents

    // Claim the pipes for an interactive expect-style session (`PtySession`) and start its raw readers,
    // returning the shared `ExpectWindow` they fill. Unlike `StartStdoutStreaming`/`StartEventStreaming`
    // this is deliberately NOT reentrant: a second session over one handle would give two matchers one
    // window, each silently consuming the other's output, so it is refused with the same
    // already-consumed error every other verb reports. The whole check + claim + setup runs under the
    // gate's one lock, so a concurrent second call observes a fully-constructed session or is refused —
    // and a session that refuses itself for its own reason (`StartContentLengthSession` on a run with no
    // piped stdout) leaves the handle unclaimed rather than poisoned.
    //
    // The readers are raw (`Pump.readTextUntilDone`), not line pumps: an interactive prompt carries no
    // line terminator, so framing the stream is precisely what must not happen here. That also means
    // `LineTerminator`, `Command.OnStdoutLine`/`OnStderrLine` and the streaming line counters have
    // nothing to observe on this path — the byte-exact tees (`StdoutTee`/`StderrTee`) still do, and are
    // fed exactly as the line pumps feed them.
    member internal _.StartInteractiveSession
        (windowChars: int, transcriptChars: int option, filterAnsi: bool)
        : Result<ExpectWindow, ProcessError> =
        gate.TryClaimInteractive(fun () ->
            Ok(sessions.StartInteractiveRawSession(windowChars, transcriptChars, filterAnsi)))

    /// Claim stdout for a Content-Length parser supplied by `ContentLengthSession`, while draining
    /// stderr independently so a chatty protocol server cannot block. The parser receives the raw
    /// stdout stream and byte-exact tee; its task becomes this handle's shared interactive outcome.
    member internal _.StartContentLengthSession
        (startStdoutPump: Stream -> Stream option -> (unit -> bool) -> Task)
        : Result<unit, ProcessError> =
        ArgumentNullException.ThrowIfNull startStdoutPump
        gate.TryClaimInteractive(fun () -> sessions.StartContentLengthSession startStdoutPump)

    /// The `CommandConfig` this handle was started from. Internal: `PtySession` reads the program name
    /// and the terminal encoding from it.
    member internal _.Config: CommandConfig = config

    /// Cancelled once this handle's own teardown begins. Internal: buffered pumps use this marker to
    /// distinguish a routine broken-pipe/close race caused by teardown from a genuine I/O failure.
    member internal _.DisposalToken: CancellationToken = terminal.DisposalToken

    /// Cancelled when a terminal/shared-exit path takes ownership of ending a streaming session. It is
    /// separate from `DisposalToken`, because a bounded Backpressure writer must wake before the shared
    /// outcome is awaited while the pump's I/O fault classification still needs the actual teardown bit.
    member internal _.BackpressureToken: CancellationToken = terminal.BackpressureToken

    /// Whether this run actually has a live pseudo-terminal behind it — what `PtySession` asks before
    /// choosing the carriage return a terminal expects for Enter over a plain pipe's line feed. Read
    /// from the spawned host (`ResizePty` is `Some` exactly for a pty-backed run) as well as the
    /// config, so a test double that models a PTY (`FakeProcess.WithPty`) answers the same as the real
    /// spawn it stands in for, rather than diverging on a config field it never set.
    member internal _.HasPseudoTerminal: bool = hasPseudoTerminal

    /// Whether this handle's bounded post-exit output drain (`PostExitDrain`) had to sever its parent
    /// read ends because something that inherited the child's stdout/stderr held the pipe open past the
    /// window. Internal, and a per-HANDLE fact rather than a process-wide counter, so a regression can
    /// assert that THIS run's capture was cut short without reading state shared with any other test.
    /// It is exactly the bit every capture ORs into its `Truncated`.
    member internal _.OutputDrainWasBounded: bool = terminal.OutputDrainWasBounded

    /// Whether even the sever could not end a pump inside the window that follows it, so it was handed
    /// to `PostExitDrain.abandon` — observed, never awaited again. Internal diagnostic: the verb's
    /// answer is identical either way, but a regression covering the uninterruptible-read path needs to
    /// prove it actually took that path rather than the ordinary sever.
    member internal _.OutputPumpsWereAbandoned: bool = terminal.OutputPumpsWereAbandoned

    /// Wait until a stdout line satisfies `predicate`, or fail with `NotReady` after `timeout`
    /// (or `Cancelled` if `cancellationToken` fires first). Consumed lines are not re-delivered; a
    /// later `StdoutLinesAsync`/`FinishAsync` sees the rest. Once a `FinishAsync` that took no stream
    /// has discarded stdout, this returns `ProcessError.Unsupported` (already consumed) rather than a
    /// `NotReady` that would read as "the line never arrived" for a stream nobody can be given.
    member this.WaitForLineAsync
        (predicate: Func<string, bool>, timeout: TimeSpan, [<Optional>] cancellationToken: CancellationToken)
        : Task<Result<string, ProcessError>> =
        ArgumentNullException.ThrowIfNull predicate

        if not (this.StartStdoutStreaming(terminalOnly = false)) then
            Task.FromResult(Error(alreadyConsumedError ()))
        else

            task {
                // Clamp so an out-of-range timeout can't throw out of the CTS constructor. The clamped
                // value is also what gets reported in NotReady below — uniform with
                // `ReadinessProbe.waitForPortUsing`/`waitForCoreUsing`: an over-long requested timeout is
                // silently capped at ~24.8 days, so reporting the raw, un-clamped value would claim a
                // budget longer than what was actually enforced.
                let armedTimeout = Timeouts.clampArmable timeout
                use timeoutCts = new CancellationTokenSource(armedTimeout, config.TimeProvider)

                use linked =
                    CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, cancellationToken)

                try
                    let mutable matched = false
                    let mutable found = Unchecked.defaultof<string>

                    while not matched do
                        let! line = sessions.ReadStdoutLineAsync linked.Token

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
                        return Error(ProcessError.NotReady(config.Program, armedTimeout))
                | :? ChannelClosedException as ex ->
                    // The stdout pump completed the channel. A clean EOF (stdout ended before a matching
                    // line) means the readiness condition was never met → NotReady. But a pump FAULT (a
                    // throwing `OnStdoutLine`/`StdoutTee` handler, or a decode/IO error) completed it WITH
                    // that exception as the InnerException; re-raise it (preserving its stack) so a real
                    // bug surfaces exactly as it does through `FinishAsync`/`StdoutLinesAsync`, rather than
                    // being masked as a spurious readiness timeout that also returns before the deadline.
                    match ex.InnerException with
                    | null -> return Error(ProcessError.NotReady(config.Program, armedTimeout))
                    | inner ->
                        ExceptionDispatchInfo.Throw inner
                        return Unchecked.defaultof<_>
            }

    /// Wait until a TCP connection to `endpoint` succeeds, or fail with `NotReady` once the shared
    /// `timeout` deadline elapses (or `Cancelled` if `cancellationToken` fires first). Every connect
    /// attempt and polling backoff shares that one deadline, so a slow or non-cooperative connect can
    /// never overrun a short `timeout` — see `ReadinessProbe.waitForCoreUsing` for the full contract,
    /// including the ratified scheduler-bounded window at the deadline. If the child exits before the
    /// port opens, the endpoint is dialled exactly once more — bounded by what is left of `timeout` and
    /// by a brief internal grace, so a port opened immediately before the child terminated is reported
    /// as `Ok` instead of being lost — and this then returns `NotReady` rather than polling out the full
    /// `timeout`; a cancelled token or an already-spent deadline still wins over that last dial. That is
    /// the same early-exit contract `WaitForHttpAsync`/`WaitForSocketAsync`/`WaitForAsync` honour, and it
    /// runs via the one reap-once exit wait the rest of the handle shares (so a later
    /// `WaitAsync`/`ProfileAsync` still reports the real exit). Background-drains (and discards) the child's piped stdout/stderr for the
    /// duration of the poll — like `WaitForLineAsync`, so a child that writes more than one OS pipe buffer
    /// of startup output (~64 KiB on Linux) before becoming ready can't block in `write()` and spuriously
    /// time out this probe — but unlike `WaitForLineAsync`, the drained bytes are discarded rather than
    /// handed back, and draining stops once the probe concludes rather than continuing as an established
    /// streaming session. A capture verb (`OutputStringAsync`/`OutputBytesAsync`/`StdoutLinesAsync`/
    /// `OutputEventsAsync`) called AFTER this probe therefore only sees what the child wrote after the
    /// probe concluded, not the full run — the same "doesn't compose with a subsequent fresh capture"
    /// limitation `WaitForLineAsync` already documents, now uniform across all five readiness probes. If
    /// a buffered/streaming verb already claimed the pipes before this call, that verb's own pump is
    /// already draining them and this probe leaves them alone (no second reader).
    member _.WaitForPortAsync
        (endpoint: IPEndPoint, timeout: TimeSpan, [<Optional>] cancellationToken: CancellationToken)
        : Task<Result<unit, ProcessError>> =
        ArgumentNullException.ThrowIfNull endpoint
        waitForPort endpoint timeout cancellationToken

    /// Wait until a connection to the Unix domain socket at `path` succeeds, or fail with `NotReady` once
    /// the shared `timeout` deadline elapses (or `Cancelled` if `cancellationToken` fires first). Behaves
    /// exactly like `WaitForPortAsync` (same deadline mechanics, same early-exit-on-child-death contract,
    /// same background stdout/stderr draining), but dials `AddressFamily.Unix` instead of TCP — see
    /// `ReadinessProbe.waitForSocket`/`waitForCoreUsing` for the full contract. Requires the host to
    /// support `AF_UNIX` sockets (Windows 10 1809+, any current Linux/macOS via .NET's own requirement);
    /// on a host without that support this returns `Error(ProcessError.Unsupported ...)` immediately,
    /// before ever attempting to dial — never a silent downgrade or an inevitable hang. A path that
    /// cannot fit the platform's Unix-socket address fails immediately with `ArgumentOutOfRangeException`
    /// rather than being retried as if no listener were present.
    member _.WaitForSocketAsync
        (path: string, timeout: TimeSpan, [<Optional>] cancellationToken: CancellationToken)
        : Task<Result<unit, ProcessError>> =
        ArgumentNullException.ThrowIfNull path

        match ReadinessProbe.unixDomainSocketsSupported (fun () -> Socket.OSSupportsUnixDomainSockets) with
        | Error err -> Task.FromResult(Error err)
        | Ok() ->
            let endpoint = UnixDomainSocketEndPoint path
            waitForSocket endpoint timeout cancellationToken

    /// Poll `uri` with HTTP GET until a response passes the default 2xx check, or fail with `NotReady`
    /// once `timeout` expires (or `Cancelled` if `cancellationToken` fires first). Connection failures,
    /// DNS failures, and request cancellations caused by the shared deadline are retried every 50ms.
    /// If the child exits before a satisfactory response arrives, exactly one more request is sent —
    /// bounded by what is left of `timeout` and by a brief internal grace — and this returns `NotReady`
    /// unless that last response is satisfactory, exactly as `WaitForPortAsync` describes.
    /// While polling, the child's piped stdout/stderr are background-drained and discarded exactly like
    /// `WaitForPortAsync`, so startup output cannot block a chatty child before it becomes ready. `uri`
    /// must be absolute; a relative URI throws `ArgumentException` before polling begins.
    member this.WaitForHttpAsync
        (uri: Uri, timeout: TimeSpan, [<Optional>] cancellationToken: CancellationToken)
        : Task<Result<unit, ProcessError>> =
        ReadinessProbe.validateAbsoluteUri uri

        this.WaitForHttpAsync(uri, ReadinessProbe.defaultHttpSuccess, timeout, cancellationToken)

    /// Like `WaitForHttpAsync(uri, timeout, cancellationToken)`, but sends requests through the
    /// caller-owned `client`. ProcessKit neither mutates nor disposes the client.
    member this.WaitForHttpAsync
        (uri: Uri, client: HttpClient, timeout: TimeSpan, [<Optional>] cancellationToken: CancellationToken)
        : Task<Result<unit, ProcessError>> =
        this.WaitForHttpAsync(uri, client, ReadinessProbe.defaultHttpSuccess, timeout, cancellationToken)

    /// Like `WaitForHttpAsync(uri, timeout, cancellationToken)`, but treats only status codes from
    /// `acceptableStatusCodes` as ready. The sequence is materialized once before polling, so every retry
    /// applies the same criteria. The sequence must contain at least one status code.
    member this.WaitForHttpAsync
        (uri: Uri, acceptableStatusCodes: seq<int>, timeout: TimeSpan, [<Optional>] cancellationToken: CancellationToken) : Task<
                                                                                                                                Result<
                                                                                                                                    unit,
                                                                                                                                    ProcessError
                                                                                                                                 >
                                                                                                                             >
        =
        ReadinessProbe.validateAbsoluteUri uri
        let isSatisfactory = httpStatusPredicate acceptableStatusCodes

        this.WaitForHttpAsync(uri, isSatisfactory, timeout, cancellationToken)

    /// Like the status-code overload, but sends requests through the caller-owned `client`. ProcessKit
    /// neither mutates nor disposes the client.
    member this.WaitForHttpAsync
        (
            uri: Uri,
            client: HttpClient,
            acceptableStatusCodes: seq<int>,
            timeout: TimeSpan,
            [<Optional>] cancellationToken: CancellationToken
        ) : Task<Result<unit, ProcessError>> =
        ReadinessProbe.validateAbsoluteUri uri
        ArgumentNullException.ThrowIfNull client
        let isSatisfactory = httpStatusPredicate acceptableStatusCodes
        this.WaitForHttpAsync(uri, client, isSatisfactory, timeout, cancellationToken)

    /// Like `WaitForHttpAsync(uri, timeout, cancellationToken)`, but uses `isSatisfactory` to inspect
    /// each response. A false result is retried; an exception from caller-supplied validation propagates.
    /// `uri` must be absolute.
    member _.WaitForHttpAsync
        (
            uri: Uri,
            isSatisfactory: Func<HttpResponseMessage, bool>,
            timeout: TimeSpan,
            [<Optional>] cancellationToken: CancellationToken
        ) : Task<Result<unit, ProcessError>> =
        ReadinessProbe.validateAbsoluteUri uri
        ArgumentNullException.ThrowIfNull isSatisfactory
        waitForHttp uri isSatisfactory timeout cancellationToken

    /// Like the predicate overload, but sends requests through the caller-owned `client`. ProcessKit
    /// neither mutates nor disposes the client.
    member _.WaitForHttpAsync
        (
            uri: Uri,
            client: HttpClient,
            isSatisfactory: Func<HttpResponseMessage, bool>,
            timeout: TimeSpan,
            [<Optional>] cancellationToken: CancellationToken
        ) : Task<Result<unit, ProcessError>> =
        ReadinessProbe.validateAbsoluteUri uri
        ArgumentNullException.ThrowIfNull client
        ArgumentNullException.ThrowIfNull isSatisfactory
        waitForHttpWithClient client uri isSatisfactory timeout cancellationToken

    /// Poll `probe` until it returns true, or fail with `NotReady` once the shared `timeout` deadline
    /// elapses (or `Cancelled` if `cancellationToken` fires first). The deadline is honored even if
    /// `probe` never completes — or blocks synchronously without ever returning a task: the invocation
    /// is isolated on the thread pool and raced against the shared deadline, and the caller's token
    /// takes priority over a concurrent success. The API cannot force a caller-owned `probe` to stop, so
    /// an abandoned invocation keeps running in the background, but its late outcome is safely observed
    /// (a late fault never becomes an unobserved task exception). See `ReadinessProbe.waitForCoreUsing` for
    /// the full contract, including the ratified scheduler-bounded window at the deadline. If the child
    /// exits before `probe` returns true, `probe` is invoked exactly once more — bounded by what is left
    /// of `timeout` and by a brief internal grace, so readiness published immediately before the child
    /// terminated is reported as `Ok` instead of being lost — and this then returns `NotReady` rather
    /// than polling out the full `timeout`. Callers therefore must expect one extra `probe` invocation
    /// after the child exits; a cancelled token or an already-spent deadline suppresses it. That is the
    /// same early-exit contract `WaitForHttpAsync`/`WaitForPortAsync` honour, and it runs via the one
    /// reap-once exit wait the rest of the handle shares (so a later `WaitAsync`/`ProfileAsync` still
    /// reports the real exit). Background-drains (and discards) the child's piped stdout/stderr for
    /// the duration of the poll, exactly like `WaitForPortAsync` — see its doc for what that does and
    /// doesn't compose with afterward.
    member _.WaitForAsync
        (probe: Func<Task<bool>>, timeout: TimeSpan, [<Optional>] cancellationToken: CancellationToken)
        : Task<Result<unit, ProcessError>> =
        ArgumentNullException.ThrowIfNull probe
        waitForCustom probe timeout cancellationToken

    /// A memoized task that waits for the process to exit (draining its pipes) without reaping it —
    /// the racing primitive behind `WaitAnyAsync`/`WaitAllAsync`. Built exactly once under the claim
    /// gate's lock (so concurrent `WaitAnyAsync`/`WaitAllAsync` on the same handle can't create two
    /// racing waits),
    /// reusing whichever consumption already owns the pipes instead of ever starting a second reader:
    /// - `StdoutStreaming`/`EventStreaming`/`Interactive`: the session's own combined outcome.
    /// - `Buffered` (a capture verb already started — the "verb, then WaitAny/WaitAll" order): the
    ///   verb's own single wait, shared via `ensureBufferedWait` (memoized under the same lock, so it
    ///   is observed here regardless of which of the two reached it first).
    /// - `Fresh` (WaitAny/WaitAll arrives first, and no readiness probe already started the shared
    ///   wait either): claims the buffered slot itself and runs its own drains, so a terminal verb
    ///   called afterwards on the same handle is refused (`alreadyConsumedError`) rather than racing
    ///   a second reader. Even here the wait itself goes through `ensureBufferedWait()`, not a fresh
    ///   `RunTerminal.WaitWithTimeout()`: a readiness probe's own early-exit detection (`waitForHttp`
    ///   et al.) can already have started the one shared `host.Wait()` while deliberately leaving the
    ///   handle `Fresh` (so a later buffered verb can still claim the pipes) — `ensureBufferedWait()`
    ///   reuses that wait when it exists, or starts the sole wait itself when this genuinely is the
    ///   first consumer, either way guaranteeing exactly one `host.Wait()`/reap per handle.
    member internal _.ExitTask: Task<Outcome> =
        // WaitAny/WaitAll are shared terminal waits. Release a bounded streaming/frame writer before
        // returning the memoized outcome task, while leaving the disposal token as the teardown marker.
        terminal.CancelBackpressureWriters()

        gate.EnsureExitTask(
            // A verb or session already owns the pipes: share ITS outcome rather than starting a second
            // reader on the same streams.
            (fun claimed ->
                match claimed with
                | Consumption.StdoutStreaming -> sessions.LineOutcome
                | Consumption.StdoutChunkStreaming -> sessions.ChunkOutcome
                | Consumption.StderrChunkStreaming -> sessions.StderrChunkOutcome
                // The event pumps already drain both pipes; reuse their shared outcome rather than
                // starting our own drains here, which would race a second reader on the same streams.
                | Consumption.EventStreaming -> sessions.EventOutcome
                // An interactive raw session already drains the pipes; reuse its shared outcome for
                // the same reason as the event session above.
                | Consumption.Interactive -> sessions.InteractiveOutcome
                // `Buffered`: a capture verb already claimed the pipes; share its single wait (memoized
                // under this same lock) rather than starting a second pair of readers and a second
                // `host.Wait()`. Its own pumps drain the pipes, so the reused wait needs none. (`Fresh`
                // never reaches here — the gate claims it below instead.)
                | _ -> ensureBufferedWait ()),
            // Nothing has claimed the pipes yet. The gate has just claimed the buffered slot for this
            // wait — so a terminal verb called after WaitAny/WaitAll on the same handle is refused
            // rather than racing a second reader — and this is its own drain-and-wait.
            (fun () ->
                task {
                    // These drains are fire-and-forget for a race loser the caller may dispose
                    // mid-drain, so they must complete quietly on teardown rather than fault unobserved.
                    let stdoutDrain = sessions.DrainStdoutQuietly()
                    let stderrDrain = sessions.DrainStderrQuietly()

                    // This handle reached WaitAny/WaitAll as its OWN terminal consumer (the claim just
                    // made), so — exactly as for a buffered verb — nobody can write its stdin any more:
                    // end an untaken `KeepStdinOpen` writer's input, or a raced child that reads to EOF
                    // never exits and never completes this wait. A no-op when there is none, and never
                    // on the branches above, where the owning consumer answers for the pipe instead.
                    finishUnclaimedStdin ()

                    // `ensureBufferedWait()`, not a raw `RunTerminal.WaitWithTimeout()`: a readiness
                    // probe may already own the one shared exit wait (see the doc comment above), and
                    // the handle staying `Fresh` until this very claim is exactly what lets that be
                    // true — so a fresh wait here would start a second, independent `host.Wait()`
                    // racing the probe's, reproducing the reap-once bug (KB K-016) the memoized wait
                    // exists to prevent. It reuses the probe's wait if one is already in flight, or
                    // starts it fresh otherwise — reentrant on the gate's lock, which is held here.
                    let! outcome = ensureBufferedWait ()
                    // Bounded like every other post-exit pump join on this handle: a
                    // `WaitAny`/`WaitAll` on a leader whose descendant inherited its stdout must
                    // resolve on the leader's own exit, not on that descendant's lifetime.
                    do! terminal.DrainPumpsBounded [| stdoutDrain; stderrDrain |]
                    // Racing this handle to exit *is* its completion (conclude does not reap, so the
                    // no-reap contract holds), so a `WaitAny`/`WaitAll`-only run still records its
                    // exit/metrics/span and clears the in-flight mark. Once-guarded, so a terminal verb
                    // afterwards (already refused by the buffered claim above) can't double-count.
                    conclude outcome
                    return outcome
                })
        )

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
        member _.DisposeAsync() = terminal.DisposeHandleAsync()

/// Guarded construction of the handle handed back to a caller once a tree has been spawned. Shared
/// by the two sites that turn an already-spawned `RunningHost` into the returned `RunningProcess`:
/// `JobRunner.start` (a private, per-run group) and `ProcessGroup.StartAsync` (a shared group).
module internal RunningProcess =

    let private build (host: RunningHost) (extraFds: (int * Stream) list) : Task<RunningProcess> =
        task {
            let constructed =
                try
                    Ok(RunningProcess(host, extraFds))
                with ex ->
                    Error ex

            match constructed with
            | Ok running -> return running
            | Error ex ->
                do! host.Teardown()
                ExceptionDispatchInfo.Throw ex
                return Unchecked.defaultof<_>
        }

    /// Build `RunningProcess host` in try/with. Constructing the handle is non-throwing in practice
    /// — its observability (`Log.spawn` / `RunTelemetryScope.Start`) swallows any sink fault, see the
    /// comment on those calls in the type above — but guard it anyway: should the constructor ever
    /// fault after the native spawn, reap the tree and release the container via `host.Teardown()`
    /// here so the child is deterministically killed/reaped instead of being orphaned to GC-time
    /// kill-on-close, then re-raise the original fault (never a silent swallow of a genuine
    /// construction bug — the caller still sees it, just without a leaked process tree).
    let buildGuarded (host: RunningHost) : Task<RunningProcess> = build host []

    let buildGuardedWithExtraFds (host: RunningHost) (extraFds: (int * Stream) list) : Task<RunningProcess> =
        build host extraFds
