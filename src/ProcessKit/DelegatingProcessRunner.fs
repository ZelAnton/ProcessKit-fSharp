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
    abstract member OutputStringAsync:
        command: Command * cancellationToken: CancellationToken -> Task<Result<ProcessResult<string>, ProcessError>>

    default _.OutputStringAsync(command, cancellationToken) =
        inner.OutputStringAsync(command, cancellationToken)

    /// Run to completion, capturing stdout as raw bytes. Override to intercept; the default forwards.
    abstract member OutputBytesAsync:
        command: Command * cancellationToken: CancellationToken -> Task<Result<ProcessResult<byte[]>, ProcessError>>

    default _.OutputBytesAsync(command, cancellationToken) =
        inner.OutputBytesAsync(command, cancellationToken)

    /// Start the command and return a live handle. Override to intercept; the default forwards.
    abstract member StartAsync:
        command: Command * cancellationToken: CancellationToken -> Task<Result<RunningProcess, ProcessError>>

    default _.StartAsync(command, cancellationToken) =
        inner.StartAsync(command, cancellationToken)

    interface IProcessRunner with
        member this.OutputStringAsync(command, cancellationToken) =
            this.OutputStringAsync(command, cancellationToken)

        member this.OutputBytesAsync(command, cancellationToken) =
            this.OutputBytesAsync(command, cancellationToken)

        member this.StartAsync(command, cancellationToken) =
            this.StartAsync(command, cancellationToken)
