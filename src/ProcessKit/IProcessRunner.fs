namespace ProcessKit

open System.Threading
open System.Threading.Tasks

/// The seam through which commands run to completion.
///
/// The default runner spawns real processes into a kill-on-drop group; test doubles
/// (`ProcessKit.Testing.ScriptedRunner`) script replies with no subprocess. This interface
/// is both the dependency-injection point and the test seam, so any .NET consumer can
/// implement or mock it with plain `Task`-returning methods.
type IProcessRunner =

    /// Run to completion, capturing stdout as decoded text. A non-zero exit is reported in
    /// the `ProcessResult`, not raised as an error.
    abstract member OutputString:
        command: Command * cancellationToken: CancellationToken -> Task<Result<ProcessResult<string>, ProcessError>>

    /// Run to completion, capturing stdout as raw bytes.
    abstract member OutputBytes:
        command: Command * cancellationToken: CancellationToken -> Task<Result<ProcessResult<byte[]>, ProcessError>>

    /// Start the command and return a live handle for streaming, interactive stdin, and waiting.
    abstract member Start:
        command: Command * cancellationToken: CancellationToken -> Task<Result<RunningProcess, ProcessError>>
