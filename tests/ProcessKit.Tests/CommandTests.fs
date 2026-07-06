namespace ProcessKit.Tests

open NUnit.Framework
open ProcessKit

[<TestFixture>]
type CommandTests() =

    [<Test>]
    member _.``create sets the program``() =
        let command = Command.create "git"
        Assert.That(command.Program, Is.EqualTo "git")

    [<Test>]
    member _.``arg and args append in order``() =
        let command =
            Command.create "git"
            |> Command.arg "rev-parse"
            |> Command.args [ "--short"; "HEAD" ]

        Assert.That(command.Arguments, Is.EqualTo(box [| "rev-parse"; "--short"; "HEAD" |]))

    [<Test>]
    member _.``the builder is immutable - each step returns a new command``() =
        let baseCommand = Command.create "git"
        let withArg = baseCommand |> Command.arg "status"
        Assert.That(baseCommand.Arguments, Is.Empty)
        Assert.That(withArg.Arguments, Is.EqualTo(box [| "status" |]))

    [<Test>]
    member _.``currentDir sets the working directory``() =
        let command = Command.create "ls" |> Command.currentDir "/tmp"
        Assert.That(command.WorkingDirectory, Is.EqualTo(Some "/tmp"))

    [<Test>]
    member _.``instance methods match the module functions``() =
        let command = Command("git").Arg("rev-parse").Args([ "--short"; "HEAD" ])
        Assert.That(command.Program, Is.EqualTo "git")
        Assert.That(command.Arguments, Is.EqualTo(box [| "rev-parse"; "--short"; "HEAD" |]))

    [<Test>]
    member _.``ProcessError.isNotFound classifies only NotFound``() =
        Assert.That(ProcessError.isNotFound (ProcessError.NotFound("git", None)), Is.True)
        Assert.That(ProcessError.isNotFound (ProcessError.Spawn("git", "x")), Is.False)
        Assert.That(ProcessError.isNotFound (ProcessError.Io "disk"), Is.False)
        Assert.That(ProcessError.isNotFound (ProcessError.Cancelled "git"), Is.False)

    [<Test>]
    member _.``ProcessError.isTransient classifies spawn and I/O errors as retriable``() =
        Assert.That(ProcessError.isTransient (ProcessError.Spawn("git", "EAGAIN")), Is.True)
        Assert.That(ProcessError.isTransient (ProcessError.Io "pipe"), Is.True)
        Assert.That(ProcessError.isTransient (ProcessError.NotFound("git", None)), Is.False)
        Assert.That(ProcessError.isTransient (ProcessError.Cancelled "git"), Is.False)

    [<Test>]
    member _.``ProcessError.IsTransient instance member classifies spawn and I/O as retriable``() =
        // The C#-friendly instance form (err.IsTransient); the module function delegates to it.
        Assert.That((ProcessError.Spawn("git", "EAGAIN")).IsTransient, Is.True)
        Assert.That((ProcessError.Io "pipe").IsTransient, Is.True)
        Assert.That((ProcessError.NotFound("git", None)).IsTransient, Is.False)
        Assert.That((ProcessError.Cancelled "git").IsTransient, Is.False)
        Assert.That((ProcessError.Exit("git", 1, "", "")).IsTransient, Is.False)

    [<Test>]
    member _.``Result.Match projects Ok and Error``() =
        let ok: Result<string, ProcessError> = Ok "hi"
        let err: Result<string, ProcessError> = Error(ProcessError.NotFound("x", None))
        Assert.That(ResultExtensions.Match(ok, (fun v -> v.ToUpperInvariant()), (fun e -> e.Message)), Is.EqualTo "HI")
        Assert.That(ResultExtensions.Match(err, (fun v -> v), (fun _ -> "fallback")), Is.EqualTo "fallback")

    [<Test>]
    member _.``Result.TryGetValue yields the value on Ok and the error on Error``() =
        match ResultExtensions.TryGetValue(Ok 42: Result<int, ProcessError>) with
        | true, v, _ -> Assert.That(v, Is.EqualTo 42)
        | _ -> Assert.Fail "expected success"

        match ResultExtensions.TryGetValue(Error(ProcessError.Cancelled "x"): Result<int, ProcessError>) with
        | false, _, e -> Assert.That(e.IsCancelled, Is.True)
        | _ -> Assert.Fail "expected failure"

    [<Test>]
    member _.``Result.GetValueOrThrow returns the value or raises ProcessException``() =
        Assert.That(ResultExtensions.GetValueOrThrow(Ok 7: Result<int, ProcessError>), Is.EqualTo 7)
        let failure = ProcessError.Cancelled "svc"
        let err: Result<int, ProcessError> = Error failure
        let mutable caught = None

        try
            ResultExtensions.GetValueOrThrow err |> ignore
        with :? ProcessException as ex ->
            caught <- Some ex

        match caught with
        | Some ex ->
            Assert.That(ex.Error.IsCancelled, Is.True)
            Assert.That(ex.Message, Is.EqualTo failure.Message) // ProcessException.Message mirrors the error
        | None -> Assert.Fail "expected ProcessException"

    [<Test>]
    member _.``Result.Switch runs exactly the matching action``() =
        let mutable seen = ""

        ResultExtensions.Switch(
            (Ok "v": Result<string, ProcessError>),
            (fun v -> seen <- "ok:" + v),
            (fun _ -> seen <- "err")
        )

        Assert.That(seen, Is.EqualTo "ok:v")

        ResultExtensions.Switch(
            (Error(ProcessError.Cancelled "x"): Result<string, ProcessError>),
            (fun _ -> seen <- "ok"),
            (fun e -> seen <- (if e.IsCancelled then "err:cancelled" else "err:other"))
        )

        Assert.That(seen, Is.EqualTo "err:cancelled")

    // ---- Priority -----------------------------------------------------------------------------

    [<Test>]
    member _.``Priority is unset by default``() =
        Assert.That(Command.create("git").Config.Priority, Is.EqualTo(None: Priority option))

    [<Test>]
    member _.``Priority records the level and the instance method matches the module function``() =
        let viaModule = Command.create "git" |> Command.priority Priority.BelowNormal
        let viaInstance = Command("git").Priority(Priority.BelowNormal)
        Assert.That(viaModule.Config.Priority, Is.EqualTo(Some Priority.BelowNormal))
        Assert.That(viaInstance.Config.Priority, Is.EqualTo(Some Priority.BelowNormal))

    [<Test>]
    member _.``Priority is immutable - setting it returns a new command``() =
        let baseCommand = Command.create "git"
        let withPriority = baseCommand |> Command.priority Priority.Idle
        Assert.That(baseCommand.Config.Priority, Is.EqualTo(None: Priority option))
        Assert.That(withPriority.Config.Priority, Is.EqualTo(Some Priority.Idle))

    [<Test>]
    member _.``Priority maps every variant to its Unix nice value``() =
        // Lower nice is higher priority; setpriority sets the absolute nice. Mirrors the Rust table.
        Assert.That(PriorityMapping.niceValue Priority.Idle, Is.EqualTo 19)
        Assert.That(PriorityMapping.niceValue Priority.BelowNormal, Is.EqualTo 10)
        Assert.That(PriorityMapping.niceValue Priority.Normal, Is.EqualTo 0)
        Assert.That(PriorityMapping.niceValue Priority.AboveNormal, Is.EqualTo -5)
        Assert.That(PriorityMapping.niceValue Priority.High, Is.EqualTo -10)

    [<Test>]
    member _.``Priority maps every variant to its Windows priority-class creation flag``() =
        // winbase.h values, OR'd into the CreateProcess creation flags.
        Assert.That(PriorityMapping.windowsCreationFlag Priority.Idle, Is.EqualTo 0x00000040u)
        Assert.That(PriorityMapping.windowsCreationFlag Priority.BelowNormal, Is.EqualTo 0x00004000u)
        Assert.That(PriorityMapping.windowsCreationFlag Priority.Normal, Is.EqualTo 0x00000020u)
        Assert.That(PriorityMapping.windowsCreationFlag Priority.AboveNormal, Is.EqualTo 0x00008000u)
        Assert.That(PriorityMapping.windowsCreationFlag Priority.High, Is.EqualTo 0x00000080u)
