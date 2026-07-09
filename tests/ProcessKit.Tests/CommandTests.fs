namespace ProcessKit.Tests

open System
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

    // ---- Umask --------------------------------------------------------------------------------

    [<Test>]
    member _.``Umask is unset by default``() =
        Assert.That(Command.create("git").Config.Umask, Is.EqualTo(None: int option))

    [<Test>]
    member _.``Umask records the mask and the instance method matches the module function``() =
        let viaModule = Command.create "git" |> Command.umask 0o022
        let viaInstance = Command("git").Umask 0o022
        Assert.That(viaModule.Config.Umask, Is.EqualTo(Some 0o022))
        Assert.That(viaInstance.Config.Umask, Is.EqualTo(Some 0o022))

    [<Test>]
    member _.``Umask is immutable - setting it returns a new command``() =
        let baseCommand = Command.create "git"
        let withUmask = baseCommand |> Command.umask 0o077
        Assert.That(baseCommand.Config.Umask, Is.EqualTo(None: int option))
        Assert.That(withUmask.Config.Umask, Is.EqualTo(Some 0o077))

    [<Test>]
    member _.``Umask accepts the boundary values 0 and 0o7777``() =
        let withZero = Command.create "git" |> Command.umask 0
        let withMax = Command.create "git" |> Command.umask 0o7777
        Assert.That(withZero.Config.Umask, Is.EqualTo(Some 0))
        Assert.That(withMax.Config.Umask, Is.EqualTo(Some 0o7777))

    [<Test>]
    member _.``Umask rejects a negative mask``() =
        Assert.Throws<ArgumentOutOfRangeException>(Action(fun () -> Command.create "git" |> Command.umask -1 |> ignore))
        |> ignore

    [<Test>]
    member _.``Umask rejects a mask beyond 0o7777``() =
        Assert.Throws<ArgumentOutOfRangeException>(
            Action(fun () -> Command.create "git" |> Command.umask 0o10000 |> ignore)
        )
        |> ignore

    // ---- Uid / Gid / User / Setsid (Unix privilege drop & session detach) ---------------------

    [<Test>]
    member _.``Uid, Gid and Setsid are unset by default``() =
        let config = Command.create("git").Config
        Assert.That(config.Uid, Is.EqualTo(None: int option))
        Assert.That(config.Gid, Is.EqualTo(None: int option))
        Assert.That(config.Setsid, Is.False)

    [<Test>]
    member _.``Uid records the value and the instance method matches the module function``() =
        let viaModule = Command.create "git" |> Command.uid 1000
        let viaInstance = Command("git").Uid 1000
        Assert.That(viaModule.Config.Uid, Is.EqualTo(Some 1000))
        Assert.That(viaInstance.Config.Uid, Is.EqualTo(Some 1000))

    [<Test>]
    member _.``Gid records the value and the instance method matches the module function``() =
        let viaModule = Command.create "git" |> Command.gid 1000
        let viaInstance = Command("git").Gid 1000
        Assert.That(viaModule.Config.Gid, Is.EqualTo(Some 1000))
        Assert.That(viaInstance.Config.Gid, Is.EqualTo(Some 1000))

    [<Test>]
    member _.``User sets both uid and gid, matching the module function``() =
        let viaModule = Command.create "git" |> Command.user 1000 2000
        let viaInstance = Command("git").User(1000, 2000)
        Assert.That(viaModule.Config.Uid, Is.EqualTo(Some 1000))
        Assert.That(viaModule.Config.Gid, Is.EqualTo(Some 2000))
        Assert.That(viaInstance.Config.Uid, Is.EqualTo(Some 1000))
        Assert.That(viaInstance.Config.Gid, Is.EqualTo(Some 2000))

    [<Test>]
    member _.``Setsid records the flag and the instance method matches the module function``() =
        let viaModule = Command.create "git" |> Command.setsid
        let viaInstance = Command("git").Setsid()
        Assert.That(viaModule.Config.Setsid, Is.True)
        Assert.That(viaInstance.Config.Setsid, Is.True)

    [<Test>]
    member _.``Uid, Gid and Setsid are immutable - setting them returns a new command``() =
        let baseCommand = Command.create "git"
        let dropped = baseCommand |> Command.user 1 1 |> Command.setsid
        Assert.That(baseCommand.Config.Uid, Is.EqualTo(None: int option))
        Assert.That(baseCommand.Config.Gid, Is.EqualTo(None: int option))
        Assert.That(baseCommand.Config.Setsid, Is.False)
        Assert.That(dropped.Config.Uid, Is.EqualTo(Some 1))
        Assert.That(dropped.Config.Gid, Is.EqualTo(Some 1))
        Assert.That(dropped.Config.Setsid, Is.True)

    [<Test>]
    member _.``Uid accepts 0 (root) but rejects a negative value``() =
        Assert.That((Command.create "git" |> Command.uid 0).Config.Uid, Is.EqualTo(Some 0))

        Assert.Throws<ArgumentOutOfRangeException>(Action(fun () -> Command.create "git" |> Command.uid -1 |> ignore))
        |> ignore

    [<Test>]
    member _.``Gid rejects a negative value``() =
        Assert.Throws<ArgumentOutOfRangeException>(Action(fun () -> Command.create "git" |> Command.gid -1 |> ignore))
        |> ignore

    [<Test>]
    member _.``User rejects a negative uid or gid``() =
        Assert.Throws<ArgumentOutOfRangeException>(Action(fun () -> Command("git").User(-1, 1) |> ignore))
        |> ignore

        Assert.Throws<ArgumentOutOfRangeException>(Action(fun () -> Command("git").User(1, -1) |> ignore))
        |> ignore

    // ---- LineTerminator -----------------------------------------------------------------------

    [<Test>]
    member _.``LineTerminator defaults to Lf on both streams``() =
        let config = Command.create("git").Config
        Assert.That(config.StdoutLineTerminator, Is.EqualTo LineTerminator.Lf)
        Assert.That(config.StderrLineTerminator, Is.EqualTo LineTerminator.Lf)

    [<Test>]
    member _.``LineTerminator sets both streams and the instance method matches the module function``() =
        let viaModule = Command.create "git" |> Command.lineTerminator LineTerminator.Any
        let viaInstance = Command("git").LineTerminator(LineTerminator.Any)

        for c in [ viaModule; viaInstance ] do
            Assert.That(c.Config.StdoutLineTerminator, Is.EqualTo LineTerminator.Any)
            Assert.That(c.Config.StderrLineTerminator, Is.EqualTo LineTerminator.Any)

    [<Test>]
    member _.``StdoutLineTerminator targets stdout only, leaving stderr at the default``() =
        let viaModule =
            Command.create "git" |> Command.stdoutLineTerminator LineTerminator.Cr

        let viaInstance = Command("git").StdoutLineTerminator(LineTerminator.Cr)

        for c in [ viaModule; viaInstance ] do
            Assert.That(c.Config.StdoutLineTerminator, Is.EqualTo LineTerminator.Cr)

            Assert.That(
                c.Config.StderrLineTerminator,
                Is.EqualTo LineTerminator.Lf,
                "stdout setter must not touch stderr"
            )

    [<Test>]
    member _.``StderrLineTerminator targets stderr only, leaving stdout at the default``() =
        let viaModule =
            Command.create "git" |> Command.stderrLineTerminator LineTerminator.CrLf

        let viaInstance = Command("git").StderrLineTerminator(LineTerminator.CrLf)

        for c in [ viaModule; viaInstance ] do
            Assert.That(c.Config.StderrLineTerminator, Is.EqualTo LineTerminator.CrLf)

            Assert.That(
                c.Config.StdoutLineTerminator,
                Is.EqualTo LineTerminator.Lf,
                "stderr setter must not touch stdout"
            )

    [<Test>]
    member _.``LineTerminator is immutable - setting it returns a new command``() =
        let baseCommand = Command.create "git"
        let withTerminator = baseCommand |> Command.lineTerminator LineTerminator.Cr
        Assert.That(baseCommand.Config.StdoutLineTerminator, Is.EqualTo LineTerminator.Lf)
        Assert.That(withTerminator.Config.StdoutLineTerminator, Is.EqualTo LineTerminator.Cr)
