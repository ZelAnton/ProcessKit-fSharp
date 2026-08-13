namespace ProcessKit

open System
open System.Threading
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

            // Every command's concurrency-slot wait and its capture go through this linked token instead
            // of `cancellationToken` directly. Under `CollectAll` it only ever fires when the caller's own
            // token does, so behaviour is unchanged; under `FailFast` the first command to land an `Error`
            // (below) also fires it, which is what lets that policy stop the REST of the batch without a
            // second, parallel notion of "cancelled" for `capture`'s callee (`Runner.outputString`/
            // `outputBytes`, and beneath them a command's own `Retry`) to special-case.
            use internalCts = CancellationTokenSource.CreateLinkedTokenSource cancellationToken
            let effectiveToken = internalCts.Token

            let runOne (command: Command) =
                task {
                    let! acquired =
                        task {
                            try
                                do! gate.WaitAsync(effectiveToken)
                                return true
                            with :? OperationCanceledException ->
                                return false
                        }

                    if not acquired then
                        return Error(ProcessError.Cancelled command.Program)
                    elif effectiveToken.IsCancellationRequested then
                        // `SemaphoreSlim.WaitAsync` can still complete successfully even after
                        // `effectiveToken` is cancelled, if the slot's release and the cancellation
                        // request race each other inside `SemaphoreSlim` itself. Honour "stop starting
                        // any command still waiting for a concurrency slot" even when that race lands
                        // this way: release the slot immediately without ever calling `capture`, so a
                        // command the FailFast contract promised to leave unstarted never performs a
                        // side effect.
                        gate.Release() |> ignore
                        return Error(ProcessError.Cancelled command.Program)
                    else
                        try
                            let! result =
                                task {
                                    try
                                        return! capture runner effectiveToken command
                                    with
                                    | :? OperationCanceledException ->
                                        return Error(ProcessError.Cancelled command.Program)
                                    | ex ->
                                        // Keep collecting: a command whose run *throws* (e.g. a throwing
                                        // OnStdoutLine handler faults the capture) becomes this element's
                                        // Error rather than faulting Task.WhenAll and discarding every
                                        // other command's result.
                                        return Error(ProcessError.Io ex.Message)
                                }

                            match policy, result with
                            | BatchPolicy.FailFast, Error _ ->
                                // `internalCts` is disposed only after every `runOne` (this one included)
                                // has returned, so it is always live here — `Cancel` is also idempotent, so
                                // a race between two failing commands cancels the batch exactly once either
                                // way, and both keep their own real errors since both had already finished.
                                try
                                    internalCts.Cancel()
                                with ex ->
                                    // `Cancel()` synchronously re-invokes every callback registered on
                                    // `effectiveToken` (including a sibling `IProcessRunner`'s own
                                    // kill-the-child registration) and re-throws if any of them faults. A
                                    // buggy registration must never fault THIS command's already-computed
                                    // `result` — and, through it, the whole batch's `Task.WhenAll` — so
                                    // swallow it here; there is nothing this batch can do to recover a
                                    // caller-owned callback's own bug, and every other command still needs
                                    // its own `Result` regardless.
                                    ignore ex
                            | _ -> ()

                            return result
                        finally
                            gate.Release() |> ignore
                }

            // Array.map preserves order, and Task.WhenAll returns results in task order.
            return! Task.WhenAll(items |> Array.map runOne)
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
