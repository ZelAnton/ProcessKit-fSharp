namespace ProcessKit.Tests

open System
open System.IO
open System.Runtime.InteropServices
open System.Threading
open System.Threading.Tasks
open NUnit.Framework
open ProcessKit

/// Test support for the Linux I/O-scheduling priority builder (`Command.IoPriority`). A top-level
/// private module rather than `let` helpers on a fixture, matching `LimitsTests`' own
/// `RlimitTestSupport`/`WindowsIoRateControlTestSupport` convention.
module private IoPriorityTestSupport =

    let isWindows = RuntimeInformation.IsOSPlatform OSPlatform.Windows
    let isLinux = RuntimeInformation.IsOSPlatform OSPlatform.Linux

    /// The encoding the kernel documents (`IOPRIO_PRIO_VALUE`), spelled out here independently of the
    /// library's own mapping so the test compares two derivations rather than one value with itself.
    let encode (classNumber: int) (level: int) = (classNumber <<< 13) ||| level

    /// A command that stays alive long enough for the parent to read the child's I/O priority back out
    /// of the kernel, on whichever shell the platform has. Only ever started on Linux.
    let liveChild () =
        Command.create "/bin/sh" |> Command.args [ "-c"; "sleep 2" ]

    /// Force the arming `ioprio_set` to fail with `errno`, run the command, and report the error it came
    /// back with. The seam is always cleared afterwards, so one test's forced failure can never leak into
    /// another's spawn (the fixtures in this assembly run sequentially, so there is no concurrent spawn to
    /// race either).
    let runWithRefusedIoprioSet (errno: int) (command: Command) : Task<ProcessError> =
        task {
            Native.Posix.ioprioSetForTests <-
                Some(fun _ ->
                    Marshal.SetLastPInvokeError errno
                    -1n)

            try
                match! command.OutputStringAsync() with
                | Error error -> return error
                | Ok result -> return failwith $"the run must be refused, but it ran ({result.Outcome})"
            finally
                Native.Posix.ioprioSetForTests <- None
        }

/// The typed Linux I/O-scheduling priority builder (T-387): the `IoPriorityClass`/`IoPriority` value API
/// and its stable name mapping, the `ioprio_set(2)` encoding, the `Command.IoPriority` builder, the
/// arming of the spawning thread that puts the priority in force before the child's first disk request,
/// and the honest typed refusals on every platform and path that cannot apply it.
[<TestFixture>]
type LinuxIoPriorityTests() =

    let isWindows = IoPriorityTestSupport.isWindows
    let isLinux = IoPriorityTestSupport.isLinux

    [<Test>]
    member _.``every class has a stable name that round-trips through TryFromName and FromName``() =
        Assert.That(IoPriorityClass.All.Count, Is.EqualTo 3, "every I/O scheduling class must be enumerable")

        for ioClass in IoPriorityClass.All do
            Assert.That(IoPriorityClass.TryFromName ioClass.Name, Is.EqualTo(Some ioClass))
            Assert.That(IoPriorityClass.FromName ioClass.Name, Is.EqualTo ioClass)

        // The exact spellings are a compatibility surface, not an implementation detail: pin them.
        let names =
            IoPriorityClass.All
            |> Seq.map (fun ioClass -> ioClass.Name)
            |> String.concat ", "

        Assert.That(names, Is.EqualTo "idle, best_effort, real_time")

    [<Test>]
    member _.``an unknown class name is an honest miss, never a silent default``() =
        // Near misses a config file realistically produces: another spelling, another case, empty.
        for miss in [ "besteffort"; "BestEffort"; "Idle"; "realtime"; "rt"; "unknown"; "" ] do
            Assert.That(IoPriorityClass.TryFromName miss, Is.EqualTo(None: IoPriorityClass option), miss)

            match Assert.Throws<ArgumentException>(Action(fun () -> IoPriorityClass.FromName miss |> ignore)) with
            | null -> Assert.Fail $"'{miss}' must be refused, not resolved to some class"
            | thrown ->
                // The typed error names every accepted spelling, so a config author can fix it from the
                // message rather than from the source.
                Assert.That(thrown.Message, Does.Contain "best_effort")
                Assert.That(thrown.Message, Does.Contain "real_time")

        Assert.Throws<ArgumentNullException>(
            Action(fun () -> IoPriorityClass.FromName Unchecked.defaultof<string> |> ignore)
        )
        |> ignore

    [<Test>]
    member _.``a level outside the kernel's range is rejected at construction, never clamped``() =
        Assert.That(IoPriority.MaxLevel, Is.EqualTo 7, "the kernel accepts levels 0..7 (IOPRIO_NR_LEVELS - 1)")

        // Every in-range level is accepted and kept exactly as given — no rounding, no clamping.
        for level in 0 .. IoPriority.MaxLevel do
            Assert.That((IoPriority.BestEffort level).Level, Is.EqualTo level)
            Assert.That((IoPriority.RealTime level).Level, Is.EqualTo level)

        for level in [ -1; 8; 42; Int32.MinValue; Int32.MaxValue ] do
            Assert.Throws<ArgumentOutOfRangeException>(Action(fun () -> IoPriority.BestEffort level |> ignore))
            |> ignore

            Assert.Throws<ArgumentOutOfRangeException>(Action(fun () -> IoPriority.RealTime level |> ignore))
            |> ignore

    [<Test>]
    member _.``each priority reports its class, and Idle has no level of its own``() =
        Assert.That(IoPriority.Idle.Class, Is.EqualTo IoPriorityClass.Idle)
        Assert.That(IoPriority.Idle.Level, Is.EqualTo 0, "the kernel ignores the level field for the idle class")
        Assert.That((IoPriority.BestEffort 7).Class, Is.EqualTo IoPriorityClass.BestEffort)
        Assert.That((IoPriority.RealTime 0).Class, Is.EqualTo IoPriorityClass.RealTime)

        // The rendering a dry run and a log line carry: the class alone where there is no level.
        Assert.That(string IoPriority.Idle, Is.EqualTo "idle")
        Assert.That(string (IoPriority.BestEffort 7), Is.EqualTo "best_effort:7")
        Assert.That(string (IoPriority.RealTime 0), Is.EqualTo "real_time:0")

    [<Test>]
    member _.``the ioprio_set encoding is the kernel's IOPRIO_PRIO_VALUE, class shifted over the level``() =
        // The three vectors ProcessKit-rs pins for the same encoding, so the two ports cannot drift.
        Assert.That(IoPriorityMapping.linuxValue IoPriority.Idle, Is.EqualTo(3 <<< 13))
        Assert.That(IoPriorityMapping.linuxValue (IoPriority.BestEffort 7), Is.EqualTo((2 <<< 13) ||| 7))
        Assert.That(IoPriorityMapping.linuxValue (IoPriority.RealTime 0), Is.EqualTo(1 <<< 13))

        // ... and every class/level pair, decomposed back into the two fields the kernel reads.
        let expectedNumber =
            dict
                [ IoPriorityClass.RealTime, 1
                  IoPriorityClass.BestEffort, 2
                  IoPriorityClass.Idle, 3 ]

        for ioClass in IoPriorityClass.All do
            for level in 0 .. IoPriority.MaxLevel do
                let priority =
                    match ioClass with
                    | IoPriorityClass.Idle -> IoPriority.Idle
                    | IoPriorityClass.BestEffort -> IoPriority.BestEffort level
                    | IoPriorityClass.RealTime -> IoPriority.RealTime level

                let value = IoPriorityMapping.linuxValue priority

                Assert.That(
                    value,
                    Is.EqualTo(IoPriorityTestSupport.encode expectedNumber[ioClass] priority.Level),
                    $"{priority}"
                )

                // The level never overflows into the class field, which is what the 0..7 validation buys.
                Assert.That(value >>> 13, Is.EqualTo expectedNumber[ioClass])
                Assert.That(value &&& 0b1_1111_1111_1111, Is.EqualTo priority.Level)

    [<Test>]
    member _.``Command.IoPriority records the request, last write wins, and the default is untouched``() =
        Assert.That(
            (Command.create "tool").Config.IoPriority,
            Is.EqualTo(None: IoPriority option),
            "a command that never asked for an I/O priority must carry none"
        )

        let command =
            Command.create "tool"
            |> Command.ioPriority (IoPriority.RealTime 2)
            |> Command.ioPriority (IoPriority.BestEffort 7)

        match command.Config.IoPriority with
        | Some priority -> Assert.That(string priority, Is.EqualTo "best_effort:7")
        | None -> Assert.Fail "the last configured I/O priority must be the one the command carries"

        Assert.Throws<ArgumentNullException>(
            Action(fun () -> (Command.create "tool").IoPriority Unchecked.defaultof<IoPriority> |> ignore)
        )
        |> ignore

    [<Test>]
    member _.``a detached launch refuses an I/O priority on every platform, never silently dropping it``() =
        // Refused for OWNERSHIP, not for a missing mechanism, so this holds on Linux too — where the
        // contained path applies the very same request successfully.
        let command =
            Command.create (if isWindows then "cmd.exe" else "/bin/sh")
            |> Command.args (if isWindows then [ "/c"; "exit 0" ] else [ "-c"; "exit 0" ])
            |> Command.ioPriority IoPriority.Idle

        match command.LaunchDetached() with
        | Error(ProcessError.Unsupported detail) ->
            Assert.That(detail, Does.Contain "IoPriority")
            Assert.That(detail, Does.Contain "detached")
        | other -> Assert.Fail $"expected a typed Unsupported for a detached launch, got {other}"

    [<Test>]
    member _.``a platform without ioprio_set refuses the spawn with a typed Unsupported``() : Task =
        task {
            if isLinux then
                Assert.Ignore "Linux applies the request; the refusal is what this asserts."

            let command =
                Command.create (if isWindows then "cmd.exe" else "/bin/sh")
                |> Command.args (if isWindows then [ "/c"; "exit 0" ] else [ "-c"; "exit 0" ])
                |> Command.ioPriority IoPriority.Idle

            match! command.OutputStringAsync() with
            | Error(ProcessError.Unsupported detail) ->
                Assert.That(detail, Does.Contain "IoPriority")
                Assert.That(detail, Does.Contain "ioprio_set")
            | other -> Assert.Fail $"expected a typed Unsupported off Linux, got {other}"
        }
        :> Task

    [<Test>]
    member _.``arming the spawning thread applies the request and puts the previous priority back``() =
        if not isLinux then
            Assert.Ignore "ioprio_set(2) is a Linux system call."

        let before =
            match Native.Posix.ioPriorityOfForTests 0 with
            | Some value -> value
            | None -> failwith "this Linux host refused to report the calling thread's own I/O priority"

        match Native.Posix.withIoPriority "tool" (IoPriority.BestEffort 7) with
        | Error error -> Assert.Fail $"arming an unprivileged best-effort priority must succeed: {error}"
        | Ok restore ->
            // While armed, the kernel reports exactly the value that would be copied into a child spawned
            // from this thread — the whole basis for the "in force before the child's first request"
            // guarantee.
            Assert.That(
                Native.Posix.ioPriorityOfForTests 0,
                Is.EqualTo(Some(IoPriorityMapping.linuxValue (IoPriority.BestEffort 7)))
            )

            restore ()

            Assert.That(
                Native.Posix.ioPriorityOfForTests 0,
                Is.EqualTo(Some before),
                "the spawning thread must be left exactly as it was found"
            )

    [<Test>]
    member _.``a kernel refusal of the requested class fails the spawn honestly, never a silent downgrade``() : Task =
        task {
            if not isLinux then
                Assert.Ignore "The refusal is raised by the Linux ioprio_set path."

            // EPERM (1) is what an unprivileged real-time request really gets; forced through the seam so
            // the assertion holds whether or not this host happens to hold CAP_SYS_ADMIN.
            let command =
                IoPriorityTestSupport.liveChild () |> Command.ioPriority (IoPriority.RealTime 0)

            match! IoPriorityTestSupport.runWithRefusedIoprioSet 1 command with
            | ProcessError.Spawn(program, detail) ->
                Assert.That(program, Is.EqualTo "/bin/sh", "the error names the program the caller asked for")
                Assert.That(detail, Does.Contain "ioprio_set")
                Assert.That(detail, Does.Contain "CAP_SYS_ADMIN")
            | other -> Assert.Fail $"expected a typed Spawn failure naming ioprio_set, got {other}"

            // The refused arming must not leave this process's thread carrying a priority it never asked
            // for: the request is read-then-set, and a failed set restores nothing because nothing was
            // applied.
            Assert.That(Native.Posix.ioPriorityOfForTests 0, Is.Not.EqualTo(Some(1 <<< 13)))
        }
        :> Task

    [<Test>]
    member _.``a kernel without ioprio_set at all is a typed Unsupported, not a spawn failure``() : Task =
        task {
            if not isLinux then
                Assert.Ignore "The ENOSYS split is raised by the Linux ioprio_set path."

            let command =
                IoPriorityTestSupport.liveChild () |> Command.ioPriority IoPriority.Idle

            // ENOSYS (38) — a kernel built without the call, or a seccomp filter answering for it.
            match! IoPriorityTestSupport.runWithRefusedIoprioSet 38 command with
            | ProcessError.Unsupported detail ->
                Assert.That(detail, Does.Contain "IoPriority")
                Assert.That(detail, Does.Contain "ENOSYS")
            | other -> Assert.Fail $"expected a typed Unsupported for a kernel without the call, got {other}"
        }
        :> Task

    [<Test>]
    member _.``the child really runs at the requested I/O priority, read back from the kernel``() : Task =
        task {
            if not isLinux then
                Assert.Ignore "ioprio_set(2) is a Linux system call."

            // Read straight out of the kernel with `ioprio_get(2)` against the child's own pid rather
            // than through `ionice`, so the assertion needs no util-linux on the host and observes what
            // the child ACTUALLY carries rather than what the parent believes it asked for.
            // The class the child ended up in, as the KERNEL reports it for that pid.
            let childIoPriority (command: Command) =
                task {
                    match! command.StartAsync() with
                    | Error error -> return failwith $"the child failed to start: {error}"
                    | Ok running ->
                        use running = running

                        match running.Pid with
                        | None -> return failwith "a live POSIX child must report its pid"
                        | Some pid -> return Native.Posix.ioPriorityOfForTests pid
                }

            // The politest class, and a levelled one — both unprivileged, so this runs as any user.
            for priority in [ IoPriority.Idle; IoPriority.BestEffort 7 ] do
                let! observed = childIoPriority (IoPriorityTestSupport.liveChild () |> Command.ioPriority priority)

                Assert.That(
                    observed,
                    Is.EqualTo(Some(IoPriorityMapping.linuxValue priority)),
                    $"the child must carry {priority}, inherited from the spawning thread"
                )

            // A command that asked for nothing is untouched: the knob must not change the default.
            let! unconfigured = childIoPriority (IoPriorityTestSupport.liveChild ())

            Assert.That(
                unconfigured,
                Is.Not.EqualTo(Some(IoPriorityMapping.linuxValue IoPriority.Idle)),
                "a command that set no I/O priority must not land in the idle class"
            )
        }
        :> Task

    [<Test>]
    member _.``a dry run reports the configured I/O priority instead of dropping it from the preview``() =
        let command =
            Command.create "tool"
            |> Command.arg "build"
            |> Command.ioPriority (IoPriority.BestEffort 7)

        Assert.That(
            ProcessKit.Testing.DryRunRunner.Render command,
            Is.EqualTo "tool build (io_priority: best_effort:7)"
        )

        // It composes with the other preview annotations rather than replacing one of them.
        let withLimits = command |> Command.rlimit RlimitResource.Core 0L 0L

        Assert.That(
            ProcessKit.Testing.DryRunRunner.Render withLimits,
            Is.EqualTo "tool build (rlimits: core=0:0) (io_priority: best_effort:7)"
        )

        // A command without one renders exactly as it always did.
        Assert.That(
            ProcessKit.Testing.DryRunRunner.Render(Command.create "tool" |> Command.arg "build"),
            Is.EqualTo "tool build"
        )

    [<Test>]
    member _.``a command carrying an I/O priority records and replays through a cassette``() : Task =
        task {
            let path =
                Path.Combine(Path.GetTempPath(), $"processkit-ioprio-cassette-{Guid.NewGuid():N}.json")

            let command =
                Command.create "tool"
                |> Command.arg "build"
                |> Command.ioPriority IoPriority.Idle

            try
                // The inner runner is the dry run: recording exercises the whole cassette path on every
                // platform without a real spawn (a Windows or macOS spawn would be refused, by design).
                let recorder =
                    ProcessKit.Testing.RecordReplayRunner.Record(path, ProcessKit.Testing.DryRunRunner())

                match! (recorder :> IProcessRunner).CaptureStringAsync(command, CancellationToken.None) with
                | Ok result -> Assert.That(result.Stdout, Does.Contain "(io_priority: idle)")
                | Error error -> Assert.Fail $"recording a command with an I/O priority failed: {error}"

                match recorder.Save() with
                | Ok() -> ()
                | Error error -> Assert.Fail $"saving the cassette failed: {error}"

                // Replay serves the recording without spawning anything, so the priority neither refuses
                // the run nor goes missing from what was recorded about it.
                match ProcessKit.Testing.RecordReplayRunner.Replay path with
                | Error error -> Assert.Fail $"loading the cassette failed: {error}"
                | Ok replayer ->
                    match! (replayer :> IProcessRunner).CaptureStringAsync(command, CancellationToken.None) with
                    | Ok result -> Assert.That(result.Stdout, Does.Contain "(io_priority: idle)")
                    | Error error -> Assert.Fail $"replaying a command with an I/O priority failed: {error}"
            finally
                if File.Exists path then
                    File.Delete path
        }
        :> Task
