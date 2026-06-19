namespace ProcessKit

open System.Runtime.CompilerServices
open System.Threading

/// Default-runner convenience verbs on `Command`, callable from F# and C# as
/// `command.Start()` / `command.Run()` etc. They use a shared `JobRunner`; for a custom or
/// injected runner, go through `Runner.*` or call the runner directly.
[<Extension>]
type CommandVerbs =

    static member val internal DefaultRunner: IProcessRunner = JobRunner()

    /// Start the command and return a live `RunningProcess`.
    [<Extension>]
    static member Start(command: Command) =
        CommandVerbs.DefaultRunner.Start(command, CancellationToken.None)

    /// Start the command, cancellable through `cancellationToken`.
    [<Extension>]
    static member Start(command: Command, cancellationToken: CancellationToken) =
        CommandVerbs.DefaultRunner.Start(command, cancellationToken)

    /// Require a zero exit and return stdout, trailing whitespace trimmed.
    [<Extension>]
    static member Run(command: Command) =
        Runner.run CommandVerbs.DefaultRunner CancellationToken.None command

    /// Run to completion, capturing stdout as decoded text (a non-zero exit is data).
    [<Extension>]
    static member OutputString(command: Command) =
        Runner.outputString CommandVerbs.DefaultRunner CancellationToken.None command

    /// Run to completion, capturing stdout as raw bytes.
    [<Extension>]
    static member OutputBytes(command: Command) =
        Runner.outputBytes CommandVerbs.DefaultRunner CancellationToken.None command
