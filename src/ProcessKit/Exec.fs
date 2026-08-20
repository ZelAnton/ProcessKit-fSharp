namespace ProcessKit

open System
open System.Collections.Generic
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks

/// How `Exec.outputAllWithPolicy` / `outputAllBytesWithPolicy` behave once any command in the batch
/// produces an `Error` — a genuine run failure (`ProcessError`: couldn't start, timed out, was
/// cancelled, exhausted its `Retry` budget, …), never a non-zero exit, which stays data under `Ok`
/// exactly as it does for `outputString`/`outputBytes` themselves.
[<RequireQualifiedAccess; NoComparison>]
type BatchPolicy =

    /// Run every command to completion regardless of any other command's outcome, and collect every
    /// result in input order — the batch never short-circuits. What `Exec.outputAll` /
    /// `outputAllBytes` do, and the default `outputAllWithPolicy` / `outputAllBytesWithPolicy` fall
    /// back to when no policy is given a reason to differ.
    | CollectAll

    /// On the FIRST command whose result is an `Error`, stop starting any command still waiting for a
    /// concurrency slot and cancel every command already running — the same signal the batch's own
    /// `CancellationToken` sends, so no `IProcessRunner` needs special-casing to honour it: a command's
    /// own `Retry` policy sees an ordinary cancellation and stops retrying exactly as it would for the
    /// caller's own token. Every element of the batch still gets a `Result` in input order:
    ///
    /// - The triggering command keeps its own real error.
    /// - A command that already finished (success or failure) before the trigger keeps its own outcome
    ///   — a `FailFast` batch is never rewritten retroactively.
    /// - A command still queued for a concurrency slot when the trigger fires never enters `capture` at
    ///   all and becomes `ProcessError.Cancelled` — this one IS guaranteed.
    /// - A command already running when the trigger fires receives the same cancellation signal as the
    ///   caller's own token, but keeps whatever result its own `capture` call returns — cancelling and
    ///   finishing race like any other cancellation, so it is NOT guaranteed to become `Cancelled`; it
    ///   may complete with its own success or failure first.
    /// - Two commands failing at nearly the same time is not a special case: whichever's `Error` is
    ///   observed first triggers the cancellation (idempotent — a second trigger is a no-op), and both
    ///   keep their own real errors, since both had already finished.
    | FailFast

/// One completed command from a completion-ordered batch stream (`Exec.outputStream` /
/// `Exec.outputStreamBytes`): the command's **input index** — its position in the `commands` sequence
/// handed to the verb — paired with that command's own independent `Result`. The index is what keeps a
/// result traceable to its source once items no longer arrive in input order, and it is always the
/// position in the ORIGINAL sequence, never a counter of how many items the stream has yielded so far.
///
/// The `Result` means exactly what one element of `Exec.outputAll` means: an `Error` is a genuine run
/// failure (`ProcessError`: couldn't start, timed out, was cancelled, exhausted its `Retry` budget, …),
/// while a non-zero exit stays `Ok` data whose `Code` you inspect.
[<Sealed>]
type BatchItem<'T> internal (index: int, result: Result<ProcessResult<'T>, ProcessError>) =

    /// This command's position in the `commands` sequence the batch verb was given (0-based).
    member _.Index = index

    /// This command's own independent result — a non-zero exit is `Ok` data, not an `Error`.
    member _.Result = result

/// The two pieces of the bounded-concurrency contract that BOTH batch fan-outs — the buffering
/// `Exec.outputAll` family and the completion-ordered `Exec.outputStream` family — have to implement
/// identically: acquiring a concurrency slot without ever starting a command the batch has already
/// given up on, and running one command inside the per-command exception boundary that turns any
/// failure into that command's own `Result` instead of faulting the whole fan-out.
///
/// Deliberately only these two primitives, not a shared driver. The two fan-outs SCHEDULE differently
/// (one collects into an input-ordered array, the other hands each result to a bounded channel before
/// releasing its slot) and the buffering driver's `BatchPolicy.FailFast` behaviour is already
/// stabilized; sharing the scheduler would have meant editing that stabilized path to grow a
/// streaming hand-off. Sharing just these two changes neither driver's observable behaviour, while
/// keeping the one invariant that must never drift between them — the post-acquisition cancellation
/// recheck below — in a single place.
module internal BatchGate =

    /// Wait for one of `gate`'s concurrency slots on behalf of a batch command. `true` means the slot
    /// is held AND the batch is still supposed to start this command; `false` means nothing is held
    /// (any slot that was acquired has already been released) and the caller must report
    /// `ProcessError.Cancelled` without ever entering `capture`.
    let tryAcquireSlot (gate: SemaphoreSlim) (effectiveToken: CancellationToken) : Task<bool> =
        task {
            let! acquired =
                task {
                    try
                        do! gate.WaitAsync effectiveToken
                        return true
                    with :? OperationCanceledException ->
                        return false
                }

            if not acquired then
                return false
            elif effectiveToken.IsCancellationRequested then
                // `SemaphoreSlim.WaitAsync` can still complete successfully even after
                // `effectiveToken` is cancelled, if the slot's release and the cancellation request
                // race each other inside `SemaphoreSlim` itself. Honour "stop starting any command
                // still waiting for a concurrency slot" even when that race lands this way: release
                // the slot immediately and report not-started, so a command the cancellation contract
                // promised to leave unstarted never performs a side effect. Acquiring the slot does
                // not by itself prove the command is still supposed to run.
                gate.Release() |> ignore
                return false
            else
                return true
        }

    /// Run one command's `capture` inside the per-command exception boundary every batch verb
    /// promises: a command whose run is cancelled or *throws* becomes THIS command's own `Error`,
    /// never a fault that discards every other command's result.
    let captureGuarded
        (capture: IProcessRunner -> CancellationToken -> Command -> Task<Result<ProcessResult<'T>, ProcessError>>)
        (runner: IProcessRunner)
        (effectiveToken: CancellationToken)
        (command: Command)
        : Task<Result<ProcessResult<'T>, ProcessError>> =
        task {
            try
                return! capture runner effectiveToken command
            with
            | :? OperationCanceledException -> return Error(ProcessError.Cancelled command.Program)
            | ex ->
                // Keep collecting: a command whose run *throws* (e.g. a throwing OnStdoutLine handler
                // faults the capture) becomes this element's Error rather than faulting the fan-out
                // and discarding every other command's result.
                return Error(ProcessError.Io ex.Message)
        }

/// The `IAsyncEnumerator<BatchItem<'T>>` behind `Exec.outputStream` / `outputStreamBytes`: the
/// completion-ordered bounded fan-out itself. One enumerator drives one whole batch run.
///
/// Hand-written rather than an `async seq { }`/`taskSeq { }` builder for the same reason as
/// `JsonLinesEnumerator`: neither ships in this project's dependencies, and this is the plain
/// `IAsyncEnumerator<'T>` shape the BCL itself expects.
///
/// The shape, and why:
///
/// - **Nothing runs until the consumer pulls.** The fan-out starts on the FIRST `MoveNextAsync`, not
///   when the verb builds the stream — a stream that has not been enumerated has spawned nothing.
/// - **A fixed worker pool, gated exactly like `Exec.runAll`.** At most `concurrency` workers claim
///   commands and wait for a slot through the shared `BatchGate.tryAcquireSlot`, so a large batch does
///   not create one task per input element and the concurrency/cancellation guarantees cannot drift
///   from the buffering verbs'.
/// - **Backpressure by construction.** A finished command hands its item to a channel bounded at the
///   concurrency cap and releases its slot only afterwards, so a consumer that stops reading
///   eventually stops the fan-out instead of letting it run the whole batch ahead into memory.
/// - **Cancellation is data, not an exception.** `runToken` (the caller's token linked with the
///   enumerator's own) cancels in-flight captures and stops queued commands from starting, but every
///   command still yields exactly one item — a cancelled one carries `ProcessError.Cancelled` — so
///   `MoveNextAsync` never throws `OperationCanceledException` for it, matching what `outputAll`
///   already returns for the same batch.
/// - **Abandoning the stream is this port's `Drop`.** `DisposeAsync` cancels the in-flight captures
///   (an own-group runner kills each live tree), leaves every still-queued command unstarted, and
///   awaits the fan-out before disposing its primitives — so an abandoned batch never leaves a
///   detached task writing into a disposed gate.
type internal BatchStreamEnumerator<'T>
    (
        concurrency: int,
        runner: IProcessRunner,
        items: Command[],
        capture: IProcessRunner -> CancellationToken -> Command -> Task<Result<ProcessResult<'T>, ProcessError>>,
        batchToken: CancellationToken,
        enumeratorToken: CancellationToken
    ) =

    // One slot per allowed concurrent command — the same gate the buffering fan-out uses, acquired
    // through the same shared helper (see `BatchGate`).
    let gate = new SemaphoreSlim(concurrency, concurrency)

    // Bounded at the concurrency cap. Together with "hand the item over BEFORE releasing the slot"
    // below, this is the whole backpressure contract: a consumer that stops reading first fills the
    // channel, then parks the commands that have finished, and finally starves the queued ones of
    // slots — nothing runs far ahead of what is actually being consumed. `SingleReader` because an
    // `IAsyncEnumerator` has exactly one consumer by contract; `SingleWriter = false` because every
    // command writes its own item.
    let results =
        Channel.CreateBounded<BatchItem<'T>>(
            BoundedChannelOptions(
                concurrency,
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait
            )
        )

    // Cancels the RUNS: the caller's own batch token, plus whatever token the consumer passed to
    // `GetAsyncEnumerator` (`await foreach (... .WithCancellation(tok))`), plus this enumerator's own
    // disposal. Honouring the enumerator's token here is why a `WithCancellation` consumer is not
    // silently ignored; it cancels the batch exactly like the verb's own token does.
    let runCts =
        CancellationTokenSource.CreateLinkedTokenSource(batchToken, enumeratorToken)

    // Cancels a command PARKED ON THE HAND-OFF when the consumer walks away. Deliberately separate
    // from `runCts`: a cancelled batch must still deliver its remaining items (that is how a queued
    // command's `Cancelled` result reaches the consumer at all), so the run-cancelling token must
    // never abort the channel write. Only disposal — nobody left to hand anything to — does.
    let disposalCts = new CancellationTokenSource()

    let runToken = runCts.Token
    let disposalToken = disposalCts.Token

    let mutable current = Unchecked.defaultof<BatchItem<'T>>
    let mutable fanout: Task option = None
    let mutable disposed = false
    let mutable nextIndex = -1

    // Cancel one of this enumerator's own token sources without letting a caller-owned callback's bug
    // escape. `Cancel()` synchronously re-invokes every callback registered on the token (including a
    // sibling `IProcessRunner`'s own kill-the-child registration) and re-throws if any of them faults;
    // there is nothing this teardown can do to recover such a bug, and the rest of the teardown — the
    // second cancellation, awaiting the fan-out, disposing the primitives — still has to happen.
    let cancelQuietly (source: CancellationTokenSource) =
        try
            source.Cancel()
        with ex ->
            ignore ex

    // One worker repeatedly claims a command, waits for a slot, captures it, hands the item to the
    // consumer, and frees the slot. The hand-off happens BEFORE the release (backpressure, above)
    // and the release is in a `finally` so an abandoned stream cannot leak the slot. Keeping the
    // index claim inside this fixed worker pool is what prevents a large batch from creating one
    // waiting task per command.
    let runWorker () : Task<unit> =
        task {
            try
                let mutable running = true

                while running do
                    let index = Interlocked.Increment(&nextIndex)

                    if index >= items.Length then
                        running <- false
                    else
                        let command = items[index]
                        let! started = BatchGate.tryAcquireSlot gate runToken

                        let! result =
                            if started then
                                BatchGate.captureGuarded capture runner runToken command
                            else
                                Task.FromResult(Error(ProcessError.Cancelled command.Program))

                        try
                            do! results.Writer.WriteAsync(BatchItem<'T>(index, result), disposalToken)
                        finally
                            if started then
                                gate.Release() |> ignore
            with :? OperationCanceledException ->
                // Only reachable from the hand-off above, and only once `DisposeAsync` has cancelled
                // `disposalToken`: the consumer abandoned the stream while a result was still waiting
                // for room. There is nobody left to hand it to, and `DisposeAsync` awaits this worker,
                // so ending quietly IS the teardown — faulting here would only produce an exception no
                // one can observe. (`BatchGate.captureGuarded` already turned a cancelled RUN into
                // this command's own `Cancelled` result, so that path never reaches here.)
                ()
        }

    // Start the whole fan-out. Called once, from the first `MoveNextAsync`. The worker count is bounded
    // by the concurrency cap, independently of the number of input commands.
    let start () =
        let workers = Array.init (min concurrency items.Length) (fun _ -> runWorker ())
        let workersFinished = new CancellationTokenSource()
        let workerCompletion = Task.WhenAll workers

        // Cancellation completes every still-unclaimed command as data even while all fixed workers
        // remain inside captures that have not finished reacting to the token. This single coordinator
        // preserves the stream's completion-order contract without recreating one waiter task per input.
        let cancelUnstartedWhenRunStops =
            task {
                use stopWaiting =
                    CancellationTokenSource.CreateLinkedTokenSource(runToken, workersFinished.Token)

                try
                    try
                        do! Task.Delay(Timeout.InfiniteTimeSpan, stopWaiting.Token)
                    with :? OperationCanceledException ->
                        // Either the run was cancelled or every worker finished normally. The token
                        // state below distinguishes those terminal states; the wait itself has no result.
                        ()

                    if runToken.IsCancellationRequested then
                        let mutable running = true

                        while running do
                            let index = Interlocked.Increment(&nextIndex)

                            if index >= items.Length then
                                running <- false
                            else
                                let command = items[index]

                                do!
                                    results.Writer.WriteAsync(
                                        BatchItem<'T>(index, Error(ProcessError.Cancelled command.Program)),
                                        disposalToken
                                    )
                with :? OperationCanceledException ->
                    // The consumer disposed the stream while this coordinator was waiting for room in
                    // the hand-off. Nobody remains to receive cancellation items, so ending the one
                    // coordinator is the teardown; claimed indices cannot subsequently start.
                    ()
            }

        fanout <-
            task {
                use _workersFinishedCts = workersFinished

                try
                    let! firstCompleted = Task.WhenAny(workerCompletion, cancelUnstartedWhenRunStops)

                    if obj.ReferenceEquals(firstCompleted, workerCompletion) then
                        cancelQuietly workersFinished

                    let! _ = Task.WhenAll(workerCompletion, cancelUnstartedWhenRunStops)
                    results.Writer.TryComplete() |> ignore
                with ex ->
                    // `runWorker` turns every per-command failure into that command's own `Result` and
                    // swallows its own disposal race, so reaching here means an unexpected fault.
                    // Complete the channel WITH the cause, so the consumer sees it from
                    // `MoveNextAsync` instead of a silently short stream.
                    results.Writer.TryComplete ex |> ignore
            }
            :> Task
            |> Some

    interface IAsyncEnumerator<BatchItem<'T>> with
        member _.Current = current

        member _.MoveNextAsync() : ValueTask<bool> =
            if fanout.IsNone && not disposed then
                start ()

            let body =
                task {
                    let mutable outcome = ValueNone

                    while outcome.IsNone do
                        // No token: cancelling the batch must not truncate the stream — every command
                        // still owes the consumer exactly one item, and a cancelled one owes it a
                        // `Cancelled` result. The wait ends when the fan-out completes the channel.
                        let! canRead = results.Reader.WaitToReadAsync()

                        if not canRead then
                            outcome <- ValueSome false
                        else
                            match results.Reader.TryRead() with
                            | true, item ->
                                current <- item
                                outcome <- ValueSome true
                            | _ ->
                                // `WaitToReadAsync` can report readable and still lose the item to a
                                // completion racing the read; loop rather than reporting an early end.
                                ()

                    return
                        match outcome with
                        | ValueSome moved -> moved
                        | ValueNone -> invalidOp "Loop invariant violated: outcome is set before the loop exits"
                }

            ValueTask<bool> body

        member _.DisposeAsync() : ValueTask =
            let body =
                task {
                    if not disposed then
                        disposed <- true

                        // The consumer is done with this batch, whether it drained the stream or
                        // walked away mid-fan-out. Cancel the runs (an own-group runner kills each
                        // live tree; a command still waiting for a slot never starts), then release
                        // anything parked on the hand-off, then wait for the fan-out to actually
                        // finish before disposing what it is still using.
                        //
                        // The order of the two cancellations is load-bearing, not cosmetic: a parked
                        // hand-off frees its concurrency slot on the way out, so releasing those FIRST
                        // would hand a slot to a queued command while the runs were still live — and
                        // an abandoned stream would start a command it promised never to start.
                        cancelQuietly runCts
                        cancelQuietly disposalCts

                        match fanout with
                        | Some pending -> do! pending
                        | None -> ()

                        // A `MoveNextAsync` after disposal (or on a never-started enumerator) must end
                        // the sequence rather than wait forever on a channel nobody will complete.
                        results.Writer.TryComplete() |> ignore

                        gate.Dispose()
                        runCts.Dispose()
                        disposalCts.Dispose()
                }

            ValueTask(body :> Task)

/// The `IAsyncEnumerable<BatchItem<'T>>` that `Exec.outputStream` / `outputStreamBytes` return. Each
/// enumeration drives its own independent fan-out over the same already-validated commands — like any
/// other `IAsyncEnumerable`, this is a factory, not a single-shot stream, so enumerating twice runs
/// the batch twice.
type internal BatchStream<'T>
    (
        concurrency: int,
        runner: IProcessRunner,
        items: Command[],
        capture: IProcessRunner -> CancellationToken -> Command -> Task<Result<ProcessResult<'T>, ProcessError>>,
        batchToken: CancellationToken
    ) =

    interface IAsyncEnumerable<BatchItem<'T>> with
        member _.GetAsyncEnumerator(cancellationToken: CancellationToken) : IAsyncEnumerator<BatchItem<'T>> =
            new BatchStreamEnumerator<'T>(concurrency, runner, items, capture, batchToken, cancellationToken)
            :> IAsyncEnumerator<BatchItem<'T>>

/// Top-level conveniences: run a program by name (without first building a `Command`), and run a
/// whole batch of commands with bounded concurrency. The single-command verbs are zero-config
/// one-liners (for cancellation, build a `Command` and use its verbs, or go through `Runner`); the
/// batch verbs take an explicit `CancellationToken` so a long fan-out can be cancelled.
[<RequireQualifiedAccess>]
module Exec =

    /// Resolve `program` to a full path without spawning it — a preflight/`doctor`-style check
    /// ("is this tool installed?") with no side effects, unlike probing availability by actually
    /// running the program (`ProbeAsync`). Reuses the exact PATH/PATHEXT-aware logic the spawn path
    /// itself falls back on to name the directories it searched (`Native.Common.resolveProgram`), so
    /// `which` and an actual spawn of the same `program` never disagree on found-vs-not-found. Returns
    /// the resolved full path on success, or a typed `ProcessError.NotFound` — `Searched` names the
    /// `PATH` value that was probed when `program` is a bare name (e.g. `"git"`), and is `None` when
    /// `program` already names a path (e.g. `"./tool"`, `"/usr/bin/tool"`), since a path-form program
    /// is checked directly and never searched.
    ///
    /// **Resolves against the CURRENT PROCESS's `PATH`** (and no prefer-local) — the host-wide "is this
    /// tool installed" question. For "will THIS command find its program", against a command's effective
    /// child `PATH` (its `Env` override) and `PreferLocal`, use `Command.ResolveProgram` /
    /// `CliClient.ResolveProgram` instead; both share this same resolver, differing only in whose `PATH`
    /// is searched.
    let which (program: string) : Result<string, ProcessError> =
        ArgumentNullException.ThrowIfNull program
        Native.Common.resolveProgram program

    /// Run `program` with `args` in a private kill-on-dispose group, require a zero/accepted exit,
    /// and return stdout with trailing whitespace trimmed.
    let run (program: string) (args: seq<string>) =
        (Command.create program |> Command.args args).RunAsync()

    /// Run `program` with `args` to completion and return the full `ProcessResult` (a non-zero exit
    /// is data, not an error).
    let outputString (program: string) (args: seq<string>) =
        (Command.create program |> Command.args args).OutputStringAsync()

    /// The raw-bytes companion to `outputString` — captures `program`'s stdout as bytes.
    let outputBytes (program: string) (args: seq<string>) =
        (Command.create program |> Command.args args).OutputBytesAsync()

    /// Launch `program` with `args` **outside all containment** and let it go — the one-liner form of
    /// `Command.LaunchDetached`, which documents the full contract. Unlike every other verb here the
    /// child runs in no kill-on-dispose group (Windows: no Job Object; POSIX: its own `setsid` session),
    /// is never waited on, and outlives this process; all you get back is its pid + start-time identity.
    /// Reach for it only for genuine spawn-and-forget work (a self-updater, a restart-myself relaunch, a
    /// daemon handed off to the OS) — for anything you want to observe, use `run`/`outputString` instead.
    /// Synchronous, like `which`: there is no run to await.
    let detach (program: string) (args: seq<string>) : Result<DetachedProcess, ProcessError> =
        (Command.create program |> Command.args args).LaunchDetached()

    // Validate and materialize a batch before starting any capture. This keeps programmer errors out
    // of the per-command exception boundary, where they would otherwise be misreported as `Io`.
    let private prepareBatch (concurrency: int) (runner: IProcessRunner) (commands: seq<Command>) : Command[] =
        ArgumentOutOfRangeException.ThrowIfLessThan(concurrency, 1, nameof concurrency)
        ArgumentNullException.ThrowIfNull(runner, nameof runner)
        ArgumentNullException.ThrowIfNull(commands, nameof commands)

        let items = Seq.toArray commands

        if items |> Array.exists (fun command -> obj.ReferenceEquals(command, null)) then
            raise (ArgumentException("commands must not contain a null element", nameof commands))

        items

    // Run every command through `runner`, capping how many are live at once, and collect a `Result` per
    // command in input order. `capture` selects the text / bytes verb and receives the EFFECTIVE token
    // (below) rather than closing over the caller's token directly, so `BatchPolicy.FailFast` can widen
    // what "cancelled" means for the rest of the batch without `capture`'s callers knowing the
    // difference. `CollectAll` never short-circuits; `FailFast` does, on the batch's first `Error`.
    let private runAll
        (concurrency: int)
        (runner: IProcessRunner)
        (items: Command[])
        (policy: BatchPolicy)
        (cancellationToken: CancellationToken)
        (capture: IProcessRunner -> CancellationToken -> Command -> Task<Result<ProcessResult<'T>, ProcessError>>)
        : Task<Result<ProcessResult<'T>, ProcessError>[]> =
        task {
            use gate = new SemaphoreSlim(concurrency, concurrency)
            let results = Array.zeroCreate<Result<ProcessResult<'T>, ProcessError>> items.Length
            let mutable nextIndex = -1

            // Every command's concurrency-slot wait and its capture go through this linked token instead
            // of `cancellationToken` directly. Under `CollectAll` it only ever fires when the caller's own
            // token does, so behaviour is unchanged; under `FailFast` the first command to land an `Error`
            // (below) also fires it, which is what lets that policy stop the REST of the batch without a
            // second, parallel notion of "cancelled" for `capture`'s callee (`Runner.outputString`/
            // `outputBytes`, and beneath them a command's own `Retry`) to special-case.
            use internalCts = CancellationTokenSource.CreateLinkedTokenSource cancellationToken
            let effectiveToken = internalCts.Token

            let runWorker () =
                task {
                    let mutable running = true

                    while running do
                        let index = Interlocked.Increment(&nextIndex)

                        if index >= items.Length then
                            running <- false
                        else
                            let command = items[index]

                            // The slot wait, including the post-acquisition cancellation recheck that
                            // keeps a command the FailFast contract promised to leave unstarted from
                            // ever performing a side effect, is the one shared with the
                            // completion-ordered fan-out — see `BatchGate.tryAcquireSlot` for why that
                            // recheck exists and why only these primitives are shared between the two
                            // drivers.
                            let! acquired = BatchGate.tryAcquireSlot gate effectiveToken

                            if not acquired then
                                results[index] <- Error(ProcessError.Cancelled command.Program)
                            else
                                try
                                    let! result = BatchGate.captureGuarded capture runner effectiveToken command

                                    match policy, result with
                                    | BatchPolicy.FailFast, Error _ ->
                                        // `internalCts` is disposed only after every worker (this one
                                        // included) has returned, so it is always live here — `Cancel`
                                        // is also idempotent, so a race between two failing commands
                                        // cancels the batch exactly once either way, and both keep their
                                        // own real errors since both had already finished.
                                        try
                                            internalCts.Cancel()
                                        with ex ->
                                            // `Cancel()` synchronously re-invokes every callback
                                            // registered on `effectiveToken` (including a sibling
                                            // `IProcessRunner`'s own kill-the-child registration) and
                                            // re-throws if any of them faults. A buggy registration must
                                            // never fault THIS command's already-computed `result` — and,
                                            // through it, the whole batch — so swallow it here; there is
                                            // nothing this batch can do to recover a caller-owned
                                            // callback's own bug, and every other command still needs
                                            // its own `Result` regardless.
                                            ignore ex
                                    | _ -> ()

                                    results[index] <- result
                                finally
                                    gate.Release() |> ignore
                }

            // A fixed worker pool keeps scheduler state O(concurrency); each worker claims a unique
            // input index, so assigning into the results array preserves input order without one task
            // per command.
            let workers = Array.init (min concurrency items.Length) (fun _ -> runWorker ())
            let! _ = Task.WhenAll workers
            return results
        }

    /// Run every command in `commands` through `runner`, keeping at most `concurrency` live at once,
    /// and collect all results (decoded text) in input order. Each element is one command's
    /// independent `Result`; the batch never short-circuits on a failure — `BatchPolicy.CollectAll`.
    /// For an explicit fail-fast policy, use `outputAllWithPolicy`.
    let outputAll
        (concurrency: int)
        (runner: IProcessRunner)
        (commands: seq<Command>)
        (cancellationToken: CancellationToken)
        =
        let items = prepareBatch concurrency runner commands

        runAll concurrency runner items BatchPolicy.CollectAll cancellationToken (fun r tok c ->
            Runner.outputString r tok c)

    /// The raw-bytes companion to `outputAll` — captures each command's stdout as bytes.
    let outputAllBytes
        (concurrency: int)
        (runner: IProcessRunner)
        (commands: seq<Command>)
        (cancellationToken: CancellationToken)
        =
        let items = prepareBatch concurrency runner commands

        runAll concurrency runner items BatchPolicy.CollectAll cancellationToken (fun r tok c ->
            Runner.outputBytes r tok c)

    /// Like `outputAll`, but with an explicit `BatchPolicy`: `BatchPolicy.CollectAll` behaves exactly
    /// like `outputAll` itself; `BatchPolicy.FailFast` stops the batch on its first `Error` (see
    /// `BatchPolicy` for the full contract — already-started/not-yet-started commands, cancellation
    /// precedence, input order). Route through the verb layer (not the raw seam) so each command's own
    /// `Retry` policy applies, matching `cmd.OutputStringAsync()` / `CliClient.OutputStringAsync` —
    /// retry still fires only on a genuine error, never on a non-zero exit (which stays data), and a
    /// `FailFast`-triggered cancellation reaches an in-flight retry loop exactly like the caller's own
    /// `cancellationToken` would.
    let outputAllWithPolicy
        (concurrency: int)
        (runner: IProcessRunner)
        (commands: seq<Command>)
        (policy: BatchPolicy)
        (cancellationToken: CancellationToken)
        =
        let items = prepareBatch concurrency runner commands
        // `BatchPolicy` is a reference type at the IL level, so a C# (or any non-F#) caller can pass
        // `null` for it; without this guard `null` falls through the `runAll` match's wildcard case
        // exactly like `CollectAll` does, silently disabling FailFast instead of failing loudly.
        ArgumentNullException.ThrowIfNull(policy :> obj, nameof policy)
        runAll concurrency runner items policy cancellationToken (fun r tok c -> Runner.outputString r tok c)

    /// The raw-bytes companion to `outputAllWithPolicy` — captures each command's stdout as bytes.
    let outputAllBytesWithPolicy
        (concurrency: int)
        (runner: IProcessRunner)
        (commands: seq<Command>)
        (policy: BatchPolicy)
        (cancellationToken: CancellationToken)
        =
        let items = prepareBatch concurrency runner commands
        // See `outputAllWithPolicy`: `null` must fail loudly at the boundary, not fall through the
        // `runAll` match's wildcard case as a silent `CollectAll`.
        ArgumentNullException.ThrowIfNull(policy :> obj, nameof policy)
        runAll concurrency runner items policy cancellationToken (fun r tok c -> Runner.outputBytes r tok c)

    /// Run every command in `commands` through `runner`, keeping at most `concurrency` live at once,
    /// and yield each result **the moment that command finishes** — the streaming sibling of
    /// `outputAll`. Same bounded fan-out, same per-command error semantics (an `Error` is a genuine run
    /// failure; a non-zero exit stays `Ok` data), same eager argument validation (a null runner /
    /// commands / command element, or `concurrency < 1`, throws right here, before anything runs) —
    /// presented as an `IAsyncEnumerable` over completions instead of one array at the end.
    ///
    /// Key differences from `outputAll`:
    ///
    /// - **Completion order, not input order.** A fast command never waits behind a slow one. Each
    ///   `BatchItem` carries its command's `Index` — its position in `commands` — so a result stays
    ///   traceable to its source; for the input-ordered array, use `outputAll`.
    /// - **Results survive a mid-fan-out cancellation.** Every item already handed to the consumer is
    ///   the consumer's, unlike `outputAll`'s array, which materializes only when the whole batch is
    ///   done.
    /// - **Nothing runs until you enumerate.** The fan-out starts on the first `MoveNextAsync`, and
    ///   every enumeration starts its own: this is an `IAsyncEnumerable` factory, not a single-shot
    ///   stream, so enumerating twice runs the batch twice.
    /// - **Backpressure.** A finished command hands its item over before releasing its concurrency
    ///   slot, and the hand-off buffer is bounded at `concurrency`, so a consumer that stops reading
    ///   stops the fan-out — once the buffer and the live commands are full, nothing further starts —
    ///   instead of letting it run the whole batch ahead into memory.
    ///
    /// **Cancellation.** `cancellationToken` — and the token a consumer passes to
    /// `GetAsyncEnumerator` / `WithCancellation`, which is honoured identically — cancels every
    /// in-flight capture and stops any command still waiting for a slot from ever starting. It does
    /// **not** truncate the stream and never surfaces as an `OperationCanceledException`: a cancelled
    /// run is data here, exactly as in `outputAll`, so every command still yields exactly one item and
    /// a command that never started yields `ProcessError.Cancelled`. Enumerate to the end to see them.
    ///
    /// **Abandoning the stream** (breaking out of the loop, which disposes the enumerator) cancels the
    /// in-flight captures — with an own-group runner (`JobRunner`) that kills each live tree; with a
    /// shared-group runner (`ProcessGroup`) they live until you tear the group down — and leaves every
    /// still-queued command unstarted. Disposal awaits the fan-out's teardown, so nothing outlives it.
    ///
    /// **No `BatchPolicy` here, by design.** This first iteration is `BatchPolicy.CollectAll`-only: the
    /// stream never short-circuits on a command's `Error`, and there is no `policy` parameter to pass
    /// (rather than one that is quietly ignored). A consumer that wants to stop on the first failure
    /// already can, and more directly than a policy would: stop enumerating, and abandonment does the
    /// rest. If you want the fail-fast contract *with* an input-ordered array, use
    /// `outputAllWithPolicy` / `outputAllBytesWithPolicy`.
    ///
    /// Each command runs through the verb layer (`Runner.outputString`), not the raw capture seam, so
    /// its own `Retry` policy applies exactly as it does in `outputAll`.
    let outputStream
        (concurrency: int)
        (runner: IProcessRunner)
        (commands: seq<Command>)
        (cancellationToken: CancellationToken)
        : IAsyncEnumerable<BatchItem<string>> =
        let items = prepareBatch concurrency runner commands

        BatchStream<string>(concurrency, runner, items, (fun r tok c -> Runner.outputString r tok c), cancellationToken)
        :> IAsyncEnumerable<BatchItem<string>>

    /// The raw-bytes companion to `outputStream` — captures each command's stdout as bytes (for binary
    /// artifacts: `git cat-file`, `tar -c`, an image transcoder). Scheduling, completion ordering,
    /// per-item indexing, validation, backpressure, and the cancellation/abandonment contract are
    /// identical to `outputStream`; the buffering counterpart is `outputAllBytes`.
    let outputStreamBytes
        (concurrency: int)
        (runner: IProcessRunner)
        (commands: seq<Command>)
        (cancellationToken: CancellationToken)
        : IAsyncEnumerable<BatchItem<byte[]>> =
        let items = prepareBatch concurrency runner commands

        BatchStream<byte[]>(concurrency, runner, items, (fun r tok c -> Runner.outputBytes r tok c), cancellationToken)
        :> IAsyncEnumerable<BatchItem<byte[]>>
