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
    abstract member OutputString:
        command: Command * cancellationToken: CancellationToken -> Task<Result<ProcessResult<string>, ProcessError>>

    default _.OutputString(command, cancellationToken) =
        inner.OutputString(command, cancellationToken)

    /// Run to completion, capturing stdout as raw bytes. Override to intercept; the default forwards.
    abstract member OutputBytes:
        command: Command * cancellationToken: CancellationToken -> Task<Result<ProcessResult<byte[]>, ProcessError>>

    default _.OutputBytes(command, cancellationToken) =
        inner.OutputBytes(command, cancellationToken)

    /// Start the command and return a live handle. Override to intercept; the default forwards.
    abstract member Start:
        command: Command * cancellationToken: CancellationToken -> Task<Result<RunningProcess, ProcessError>>

    default _.Start(command, cancellationToken) = inner.Start(command, cancellationToken)

    interface IProcessRunner with
        member this.OutputString(command, cancellationToken) =
            this.OutputString(command, cancellationToken)

        member this.OutputBytes(command, cancellationToken) =
            this.OutputBytes(command, cancellationToken)

        member this.Start(command, cancellationToken) = this.Start(command, cancellationToken)
