namespace ProcessKit

open System
open System.Threading
open System.Threading.Tasks

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

    // Validate and materialize a batch before starting any capture. This keeps programmer errors out
    // of the per-command exception boundary, where they would otherwise be misreported as `Io`.
    let private prepareBatch (runner: IProcessRunner) (commands: seq<Command>) : Command[] =
        ArgumentNullException.ThrowIfNull(runner, nameof runner)
        ArgumentNullException.ThrowIfNull(commands, nameof commands)

        let items = Seq.toArray commands

        if items |> Array.exists (fun command -> obj.ReferenceEquals(command, null)) then
            raise (ArgumentException("commands must not contain a null element", nameof commands))

        items

    // Run every command through `runner`, capping how many are live at once, and collect ALL results
    // in input order — the batch never short-circuits. `capture` selects the text / bytes verb.
    let private runAll
        (concurrency: int)
        (runner: IProcessRunner)
        (items: Command[])
        (cancellationToken: CancellationToken)
        (capture: IProcessRunner -> Command -> Task<Result<ProcessResult<'T>, ProcessError>>)
        : Task<Result<ProcessResult<'T>, ProcessError>[]> =
        task {
            let limit = max 1 concurrency
            use gate = new SemaphoreSlim(limit, limit)

            let runOne (command: Command) =
                task {
                    let! acquired =
                        task {
                            try
                                do! gate.WaitAsync(cancellationToken)
                                return true
                            with :? OperationCanceledException ->
                                return false
                        }

                    if not acquired then
                        return Error(ProcessError.Cancelled command.Program)
                    else
                        try
                            try
                                return! capture runner command
                            with
                            | :? OperationCanceledException -> return Error(ProcessError.Cancelled command.Program)
                            | ex ->
                                // Keep the batch collect-all: a command whose run *throws* (e.g. a throwing
                                // OnStdoutLine handler faults the capture) becomes this element's Error rather
                                // than faulting Task.WhenAll and discarding every other command's result.
                                return Error(ProcessError.Io ex.Message)
                        finally
                            gate.Release() |> ignore
                }

            // Array.map preserves order, and Task.WhenAll returns results in task order.
            return! Task.WhenAll(items |> Array.map runOne)
        }

    /// Run every command in `commands` through `runner`, keeping at most `concurrency` live at once,
    /// and collect all results (decoded text) in input order. Each element is one command's
    /// independent `Result`; the batch never short-circuits on a failure.
    let outputAll
        (concurrency: int)
        (runner: IProcessRunner)
        (commands: seq<Command>)
        (cancellationToken: CancellationToken)
        =
        // Route through the verb layer (not the raw seam) so each command's own `Retry` policy applies,
        // matching `cmd.OutputStringAsync()` / `CliClient.OutputStringAsync` — retry still fires only on a genuine
        // error, never on a non-zero exit (which stays data).
        let items = prepareBatch runner commands
        runAll concurrency runner items cancellationToken (fun r c -> Runner.outputString r cancellationToken c)

    /// The raw-bytes companion to `outputAll` — captures each command's stdout as bytes.
    let outputAllBytes
        (concurrency: int)
        (runner: IProcessRunner)
        (commands: seq<Command>)
        (cancellationToken: CancellationToken)
        =
        let items = prepareBatch runner commands
        runAll concurrency runner items cancellationToken (fun r c -> Runner.outputBytes r cancellationToken c)
