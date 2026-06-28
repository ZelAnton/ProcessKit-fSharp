namespace ProcessKit

open System
open System.Threading
open System.Threading.Tasks

/// A pass-through `IProcessRunner` base for decorators: it forwards all three verbs to `inner`, so a
/// wrapper — logging, retry, metrics, fault injection, fixed-latency, recording — overrides only the
/// verb(s) it changes and inherits the rest. Each verb is an overridable member; the interface
/// dispatches to it.
[<AbstractClass>]
type DelegatingProcessRunner(inner: IProcessRunner) =

    do ArgumentNullException.ThrowIfNull inner

    /// The wrapped runner.
    member _.Inner = inner

    /// Run to completion, capturing stdout as decoded text. Override to intercept; the default
    /// forwards to the wrapped runner.
    abstract member CaptureStringAsync:
        command: Command * cancellationToken: CancellationToken -> Task<Result<ProcessResult<string>, ProcessError>>

    default _.CaptureStringAsync(command, cancellationToken) =
        inner.CaptureStringAsync(command, cancellationToken)

    /// Run to completion, capturing stdout as raw bytes. Override to intercept; the default forwards.
    abstract member CaptureBytesAsync:
        command: Command * cancellationToken: CancellationToken -> Task<Result<ProcessResult<byte[]>, ProcessError>>

    default _.CaptureBytesAsync(command, cancellationToken) =
        inner.CaptureBytesAsync(command, cancellationToken)

    /// Start the command and return a live handle. Override to intercept; the default forwards.
    abstract member SpawnAsync:
        command: Command * cancellationToken: CancellationToken -> Task<Result<RunningProcess, ProcessError>>

    default _.SpawnAsync(command, cancellationToken) =
        inner.SpawnAsync(command, cancellationToken)

    interface IProcessRunner with
        member this.CaptureStringAsync(command, cancellationToken) =
            this.CaptureStringAsync(command, cancellationToken)

        member this.CaptureBytesAsync(command, cancellationToken) =
            this.CaptureBytesAsync(command, cancellationToken)

        member this.SpawnAsync(command, cancellationToken) =
            this.SpawnAsync(command, cancellationToken)
