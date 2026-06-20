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
