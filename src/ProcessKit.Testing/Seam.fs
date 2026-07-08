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

    /// Guard cancellation, then hand off to `resolve` for an already-validated command. A cancelled
    /// run is always an error and never reaches `resolve`.
    let serve
        (resolve: Command -> Result<RunningProcess, ProcessError>)
        (command: Command)
        (cancellationToken: CancellationToken)
        : Result<RunningProcess, ProcessError> =
        if cancellationToken.IsCancellationRequested then
            Error(ProcessError.Cancelled command.Program)
        else
            resolve command

    /// Build the full `IProcessRunner` seam over `resolve`, so a double's own `interface
    /// IProcessRunner` block is a one-line forward per verb instead of a byte-for-byte copy of the
    /// cancellation check and the string/bytes/handle projection.
    let runner (resolve: Command -> Result<RunningProcess, ProcessError>) : IProcessRunner =
        { new IProcessRunner with
            member _.CaptureStringAsync(command, cancellationToken) =
                match serve resolve command cancellationToken with
                | Ok running -> running.OutputStringAsync()
                | Error error -> Task.FromResult(Error error)

            member _.SpawnAsync(command, cancellationToken) =
                Task.FromResult(serve resolve command cancellationToken)

            member _.CaptureBytesAsync(command, cancellationToken) =
                match serve resolve command cancellationToken with
                | Ok running -> running.OutputBytesAsync()
                | Error error -> Task.FromResult(Error error) }
