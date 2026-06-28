namespace ProcessKit

open System.Threading
open System.Threading.Tasks

/// The seam through which commands run to completion: three low-level primitives an implementer or
/// mock provides. The public verb vocabulary (`RunAsync`/`OutputStringAsync`/`ExitCodeAsync`/… on a
/// `Command`, a runner, a `CliClient`, or a `Pipeline`) is layered on top of these by the `Runner`
/// module and its extension methods — so the primitives are named distinctly (`Capture*`/`Spawn`) and
/// never collide with the verbs. (That collision used to force the capture verbs to silently drop the
/// retry policy when called with a `CancellationToken`; with distinct names every verb overload retries
/// uniformly.)
///
/// The default runner spawns real processes into a kill-on-drop group; test doubles
/// (`ProcessKit.Testing.ScriptedRunner`) script replies with no subprocess. This interface
/// is both the dependency-injection point and the test seam, so any .NET consumer can
/// implement or mock it with plain `Task`-returning methods.
type IProcessRunner =

    /// Run to completion, capturing stdout as decoded text. A non-zero exit is reported in
    /// the `ProcessResult`, not raised as an error. (The primitive behind the `OutputStringAsync` verb.)
    abstract member CaptureStringAsync:
        command: Command * cancellationToken: CancellationToken -> Task<Result<ProcessResult<string>, ProcessError>>

    /// Run to completion, capturing stdout as raw bytes. (The primitive behind the `OutputBytesAsync` verb.)
    abstract member CaptureBytesAsync:
        command: Command * cancellationToken: CancellationToken -> Task<Result<ProcessResult<byte[]>, ProcessError>>

    /// Start the command and return a live handle for streaming, interactive stdin, and waiting.
    /// (The primitive behind the `StartAsync` verb.)
    abstract member SpawnAsync:
        command: Command * cancellationToken: CancellationToken -> Task<Result<RunningProcess, ProcessError>>
