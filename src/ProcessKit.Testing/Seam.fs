namespace ProcessKit.Testing

open System.Threading
open System.Threading.Tasks
open ProcessKit

/// Shared plumbing behind the in-memory `IProcessRunner` doubles (`ScriptedRunner`, `DryRunRunner`):
/// every verb ultimately reduces to "resolve the command to a `RunningProcess` (or fail), then
/// project that onto the verb that was called" — `CaptureStringAsync`/`CaptureBytesAsync` reduce the
/// handle to its output, `SpawnAsync` hands the handle back as-is. A double supplies only its own
/// `resolve` step (scripted-reply matching, deterministic rendering); this module owns the part that
/// must not drift between doubles — the cancellation contract (a cancelled run is always an error,
/// matching `JobRunner`/`ProcessGroup`) and the verb projection itself.
module internal Seam =

    /// Guard the one-shot spawn token, then hand off to `resolve` for an already-validated command.
    /// `CancelOn` deliberately does not apply here: a live handle is caller-driven after spawning.
    let serve
        (resolve: Command -> Result<RunningProcess, ProcessError>)
        (command: Command)
        (cancellationToken: CancellationToken)
        : Result<RunningProcess, ProcessError> =
        if cancellationToken.IsCancellationRequested then
            Error(ProcessError.Cancelled command.Program)
        else
            resolve command

    /// Run a completion verb with the same linked-token contract as a real runner. Unlike `SpawnAsync`,
    /// completion owns the running fake and therefore tears it down when either cancellation source
    /// fires — through the same `BeginCancelTeardown` seam the real runners use, so a double honours
    /// `Command.CancelGrace`/`CancelSignal` too: a `FakeProcess` records the soft signal on its `Signals`
    /// log exactly as it records a `StopAsync` one, and only escalates to the hard kill after the grace.
    let complete
        (resolve: Command -> Result<RunningProcess, ProcessError>)
        (consume: RunningProcess -> Task<Result<'a, ProcessError>>)
        (command: Command)
        (cancellationToken: CancellationToken)
        : Task<Result<'a, ProcessError>> =
        task {
            use linkedCts =
                match command.Config.CancelOn with
                | Some extra -> CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, extra)
                | None -> CancellationTokenSource.CreateLinkedTokenSource cancellationToken

            let effectiveToken = linkedCts.Token

            if effectiveToken.IsCancellationRequested then
                return Error(ProcessError.Cancelled command.Program)
            else
                match resolve command with
                | Error error -> return Error error
                | Ok running ->
                    use _registration = effectiveToken.Register(fun () -> running.BeginCancelTeardown())
                    let! result = consume running

                    if effectiveToken.IsCancellationRequested then
                        return Error(ProcessError.Cancelled command.Program)
                    else
                        return result
        }

    /// Build the full `IProcessRunner` seam over `resolve`, so a double's own `interface
    /// IProcessRunner` block is a one-line forward per verb instead of a byte-for-byte copy of the
    /// cancellation check and the string/bytes/handle projection.
    let runner (resolve: Command -> Result<RunningProcess, ProcessError>) : IProcessRunner =
        { new IProcessRunner with
            member _.CaptureStringAsync(command, cancellationToken) =
                complete resolve (fun running -> running.OutputStringAsync()) command cancellationToken

            member _.SpawnAsync(command, cancellationToken) =
                Task.FromResult(serve resolve command cancellationToken)

            member _.CaptureBytesAsync(command, cancellationToken) =
                complete resolve (fun running -> running.OutputBytesAsync()) command cancellationToken }
