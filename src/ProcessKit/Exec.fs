namespace ProcessKit

open System.Threading
open System.Threading.Tasks

/// Top-level conveniences: run a program by name (without first building a `Command`), and run a
/// whole batch of commands with bounded concurrency. Each verb takes an explicit `CancellationToken`
/// (pass `CancellationToken.None` when you don't need cancellation), matching the `Runner` module.
[<RequireQualifiedAccess>]
module Exec =

    /// Run `program` with `args` in a private kill-on-dispose group, require a zero/accepted exit,
    /// and return stdout with trailing whitespace trimmed.
    let run (program: string) (args: seq<string>) (cancellationToken: CancellationToken) =
        (Command.create program |> Command.args args).Run cancellationToken

    /// Run `program` with `args` to completion and return the full `ProcessResult` (a non-zero exit
    /// is data, not an error).
    let outputString (program: string) (args: seq<string>) (cancellationToken: CancellationToken) =
        (Command.create program |> Command.args args).OutputString cancellationToken

    /// The raw-bytes companion to `outputString` — captures `program`'s stdout as bytes.
    let outputBytes (program: string) (args: seq<string>) (cancellationToken: CancellationToken) =
        (Command.create program |> Command.args args).OutputBytes cancellationToken

    // Run every command through `runner`, capping how many are live at once, and collect ALL results
    // in input order — the batch never short-circuits. `capture` selects the text / bytes verb.
    let private runAll
        (concurrency: int)
        (runner: IProcessRunner)
        (commands: seq<Command>)
        (capture: IProcessRunner -> Command -> Task<Result<ProcessResult<'T>, ProcessError>>)
        : Task<Result<ProcessResult<'T>, ProcessError>[]> =
        task {
            let limit = max 1 concurrency
            let items = Seq.toArray commands
            use gate = new SemaphoreSlim(limit, limit)

            let runOne (command: Command) =
                task {
                    do! gate.WaitAsync()

                    try
                        return! capture runner command
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
        runAll concurrency runner commands (fun r c -> r.OutputString(c, cancellationToken))

    /// The raw-bytes companion to `outputAll` — captures each command's stdout as bytes.
    let outputAllBytes
        (concurrency: int)
        (runner: IProcessRunner)
        (commands: seq<Command>)
        (cancellationToken: CancellationToken)
        =
        runAll concurrency runner commands (fun r c -> r.OutputBytes(c, cancellationToken))
