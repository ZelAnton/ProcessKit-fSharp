namespace ProcessKit.Tests

open System
open System.Collections.Generic
open System.IO
open System.Runtime.InteropServices
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open NUnit.Framework
open NUnit.Framework.Legacy
open ProcessKit
open ProcessKit.Testing

/// A deterministic inner `IProcessRunner` whose bytes verb returns arbitrary (possibly non-UTF-8)
/// `stdout` bytes, for record-mode bytes tests. The string/spawn verbs are unused here.
type private FixedBytesRunner(stdout: byte[], stderr: string, code: int, duration: TimeSpan, truncated: bool) =

    new(stdout: byte[], stderr: string, code: int) = FixedBytesRunner(stdout, stderr, code, TimeSpan.Zero, false)

    interface IProcessRunner with
        member _.CaptureBytesAsync(command, _cancellationToken) =
            Task.FromResult(
                Ok(
                    ProcessResult<byte[]>(
                        command.Program,
                        stdout,
                        stderr,
                        Outcome.Exited code,
                        duration,
                        truncated,
                        [ 0 ],
                        stdoutEncoding = command.Config.StdoutEncoding
                    )
                )
            )

        member _.CaptureStringAsync(command, _cancellationToken) =
            Task.FromResult(
                Ok(
                    ProcessResult<string>(
                        command.Program,
                        Encoding.UTF8.GetString stdout,
                        stderr,
                        Outcome.Exited code,
                        duration,
                        truncated,
                        [ 0 ]
                    )
                )
            )

        member _.SpawnAsync(_command, _cancellationToken) =
            Task.FromResult(Error(ProcessError.Unsupported "FixedBytesRunner has no Spawn"))

/// A deterministic inner `IProcessRunner` for record-mode tests: every call returns `stdout`/`code`.
type private FixedRunner(stdout: string, code: int, duration: TimeSpan, truncated: bool) =

    new(stdout: string, code: int) = FixedRunner(stdout, code, TimeSpan.Zero, false)

    interface IProcessRunner with
        member _.CaptureStringAsync(command, _cancellationToken) =
            Task.FromResult(
                Ok(ProcessResult<string>(command.Program, stdout, "", Outcome.Exited code, duration, truncated, [ 0 ]))
            )

        member _.CaptureBytesAsync(command, _cancellationToken) =
            Task.FromResult(
                Ok(
                    ProcessResult<byte[]>(
                        command.Program,
                        Encoding.UTF8.GetBytes stdout,
                        "",
                        Outcome.Exited code,
                        duration,
                        truncated,
                        [ 0 ]
                    )
                )
            )

        member _.SpawnAsync(_command, _cancellationToken) =
            Task.FromResult(Error(ProcessError.Unsupported "FixedRunner has no Spawn"))

/// A deterministic inner `IProcessRunner` whose every verb FAILS with a scripted typed error: the
/// errors in order, then the last one again for every later call (so a fixed failure needs a
/// one-element script). `Calls` counts every call it served, which is how a test proves a replay never
/// reached the inner runner at all.
type private ErrorRunner(errors: ProcessError list) =
    let scripted = List.toArray errors
    let mutable calls = 0

    let next () =
        let index = min calls (scripted.Length - 1)
        calls <- calls + 1
        scripted[index]

    /// How many capture/spawn calls this runner has served.
    member _.Calls = calls

    interface IProcessRunner with
        member _.CaptureStringAsync(_command, _cancellationToken) =
            Task.FromResult<Result<ProcessResult<string>, ProcessError>>(Error(next ()))

        member _.CaptureBytesAsync(_command, _cancellationToken) =
            Task.FromResult<Result<ProcessResult<byte[]>, ProcessError>>(Error(next ()))

        member _.SpawnAsync(_command, _cancellationToken) =
            Task.FromResult<Result<RunningProcess, ProcessError>>(Error(next ()))

/// A deterministic inner `IProcessRunner` that answers like a REAL run does at the capture boundary:
/// `stdout` comes back only when the command's own wiring lets it reach the parent (a pipe, or a PTY's
/// merged terminal), and `stderr` only when it is neither folded into stdout (`MergeStderr`/`Pty`) nor
/// sent somewhere the parent cannot see (`Null`/`Inherit`/a direct file redirect) — the same boundary
/// `RunningProcess` and `FakeProcess.Build` apply to their streams. Recording through it therefore
/// produces the entry a real record session would, which is what makes a "recorded with stdout going
/// nowhere" fixture honest rather than synthetic. It does not model a merged run's interleaving: a
/// folded stderr is simply not returned separately.
type private WiringAwareRunner(stdout: string, stderr: string) =
    let capturedStdout (command: Command) =
        let config = command.Config

        if
            config.Pty.IsSome
            || (config.StdoutFile.IsNone && config.StdoutMode = StdioMode.Piped)
        then
            stdout
        else
            ""

    let capturedStderr (command: Command) =
        let config = command.Config

        if
            config.Pty.IsNone
            && not config.MergeStderr
            && config.StderrFile.IsNone
            && config.StderrMode = StdioMode.Piped
        then
            stderr
        else
            ""

    interface IProcessRunner with
        member _.CaptureStringAsync(command, _cancellationToken) =
            Task.FromResult(
                Ok(
                    ProcessResult<string>(
                        command.Program,
                        capturedStdout command,
                        capturedStderr command,
                        Outcome.Exited 0,
                        TimeSpan.Zero,
                        false,
                        [ 0 ]
                    )
                )
            )

        member _.CaptureBytesAsync(command, _cancellationToken) =
            Task.FromResult(
                Ok(
                    ProcessResult<byte[]>(
                        command.Program,
                        Encoding.UTF8.GetBytes(capturedStdout command),
                        capturedStderr command,
                        Outcome.Exited 0,
                        TimeSpan.Zero,
                        false,
                        [ 0 ],
                        stdoutEncoding = command.Config.StdoutEncoding
                    )
                )
            )

        member _.SpawnAsync(_command, _cancellationToken) =
            Task.FromResult(Error(ProcessError.Unsupported "WiringAwareRunner has no Spawn"))

/// An inner runner that cancels `source` and only THEN returns its typed failure — the exact race the
/// record path's cancellation gate is about: the call really has come back with a failure, but the
/// effective token is already cancellation-requested by the time the recorder looks at it.
type private CancelThenFailRunner(source: CancellationTokenSource, error: ProcessError) =
    let fail () =
        source.Cancel()
        error

    interface IProcessRunner with
        member _.CaptureStringAsync(_command, _cancellationToken) =
            Task.FromResult<Result<ProcessResult<string>, ProcessError>>(Error(fail ()))

        member _.CaptureBytesAsync(_command, _cancellationToken) =
            Task.FromResult<Result<ProcessResult<byte[]>, ProcessError>>(Error(fail ()))

        member _.SpawnAsync(_command, _cancellationToken) =
            Task.FromResult<Result<RunningProcess, ProcessError>>(Error(fail ()))

/// A tee sink whose every write fails, so a replay that feeds it must surface that failure the way a
/// real run does — the capture pump reclassifies a write `IOException` into `ProcessError.Io` — instead
/// of hiding it behind the recorded success. `FlushAsync` deliberately succeeds: the pump's final flush
/// is best-effort and swallows I/O errors, so a sink that failed only there would prove nothing.
type private FailingTeeStream() =
    inherit Stream()

    override _.CanRead = false
    override _.CanSeek = false
    override _.CanWrite = true
    override _.Length = 0L

    override _.Position
        with get () = 0L
        and set _ = ()

    override _.Flush() = ()

    override _.Read(_buffer: byte[], _offset: int, _count: int) : int =
        raise (NotSupportedException "a tee sink is write-only")

    override _.Seek(_offset: int64, _origin: SeekOrigin) : int64 =
        raise (NotSupportedException "a tee sink is not seekable")

    override _.SetLength(_value: int64) =
        raise (NotSupportedException "a tee sink is not seekable")

    override _.Write(_buffer: byte[], _offset: int, _count: int) =
        raise (IOException "the tee sink is broken")

    override _.WriteAsync(_buffer: ReadOnlyMemory<byte>, _cancellationToken: CancellationToken) : ValueTask =
        ValueTask(Task.FromException(IOException "the tee sink is broken"))

[<TestFixture>]
type CassetteTests() =

    /// Remove a cassette and the sibling advisory-lock file a save creates next to it (`Save` never
    /// deletes that file itself — see `RecordReplayRunner.Save` — so a test cleans it up like the
    /// cassette it belongs to).
    let deleteCassette (path: string) : unit =
        for file in [ path; path + ".lock" ] do
            if File.Exists file then
                File.Delete file

    let withCassette (body: string -> Task) : Task =
        task {
            let path = Path.GetTempFileName()

            try
                do! body path
            finally
                deleteCassette path
        }

    /// A cassette path in a directory of its own that does **not** exist yet, so "nothing was written"
    /// is observable as the file's absence — something `withCassette` cannot express, because
    /// `Path.GetTempFileName` creates the file it hands out. The directory and anything under it
    /// (cassette, sibling lock file, temps) goes on the way out.
    let withUnwrittenCassette (body: string -> Task) : Task =
        task {
            let directory = Path.Combine(Path.GetTempPath(), $"pk-cassette-{Guid.NewGuid():N}")

            Directory.CreateDirectory directory |> ignore

            try
                do! body (Path.Combine(directory, "cassette.json"))
            finally
                try
                    Directory.Delete(directory, true)
                with
                | :? IOException
                | :? UnauthorizedAccessException ->
                    // A temp directory something still holds a handle on: the OS reclaims it later, and
                    // a cleanup failure must not fail a test that already made its assertions.
                    ()
        }

    let runner (r: RecordReplayRunner) : IProcessRunner = r

    /// The cassette on disk, read through a share mode that tolerates the atomic replace a concurrent
    /// save performs (on Windows a plain read would otherwise make that save's rename fail).
    let readCassetteText (path: string) : string =
        use stream =
            new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite ||| FileShare.Delete)

        use reader = new StreamReader(stream)
        reader.ReadToEnd()

    /// The `Program` of every entry in the cassette on disk, in capture order — enough to tell WHICH
    /// writer's snapshot a file holds, and how much of it.
    let cassettePrograms (path: string) : string[] =
        use document = JsonDocument.Parse(readCassetteText path)

        document.RootElement.GetProperty("Entries").EnumerateArray()
        |> Seq.map (fun entry ->
            match entry.GetProperty("Program").GetString() with
            | null -> ""
            | program -> program)
        |> Seq.toArray

    /// The directory holding a cassette (a temp-file path always has one).
    let cassetteDirectory (path: string) : string =
        match Path.GetDirectoryName path with
        | null
        | "" -> "."
        | directory -> directory

    /// The file name a cassette's sibling temp files are derived from.
    let cassetteFileName (path: string) : string =
        match Path.GetFileName path with
        | null -> ""
        | fileName -> fileName

    /// Record `count` distinct invocations of `program` through a recorder, so its snapshot is
    /// recognisable on disk (every entry carries that program name).
    let recordEntries (recorder: RecordReplayRunner) (program: string) (count: int) : Task =
        task {
            for index in 1..count do
                let command = Command.create program |> Command.arg (string index)
                let! _ = (recorder :> IProcessRunner).OutputStringAsync(command, CancellationToken.None)
                ()
        }

    /// Build a recorder for the named mode over `path`, so a dispose-time behaviour can be asserted the
    /// same way for `Record` and `Auto` — the two modes that record, and which share one flush path.
    let recorderFor (mode: string) (path: string) (inner: IProcessRunner) : RecordReplayRunner =
        match mode with
        | "Record" -> RecordReplayRunner.Record(path, inner)
        | "Auto" ->
            match RecordReplayRunner.Auto(path, inner) with
            | Ok auto -> auto
            | Error error -> failwith $"auto load: {error}"
        | other -> failwith $"unknown recorder mode '{other}'"

    /// The output the crashed run below captured, named so an assertion can say what a cassette written
    /// by that crash would be leaking: captured `stdout` is stored verbatim, like `program` and `args`.
    let crashedRunOutput = "s3cr3t-token-echoed-by-the-crashed-run"

    /// The command the crashed run made — its program is what a leaked cassette would be recognisable by.
    let crashedRunCommand = Command.create "crashed-run" |> Command.arg "1"

    /// The exception that leaves the recording scope below, standing in for the assertion a test dies on.
    let unwindFailure = "the test failed before the recording was finished"

    /// `recordThenUnwind`'s two paths, named so its call sites do not read as bare booleans: the scope
    /// throws before it ever declares the recording finished, or after.
    let leftIncomplete = false

    let declaredComplete = true

    /// The shape this is all about: a `use recorder = …` scope that records a call and is then left by a
    /// thrown exception, so `Dispose` runs while the stack unwinds. `complete` decides whether the
    /// recording is declared finished first — the only difference between the two paths. Hands back the
    /// exception that escaped, so a test can also prove `Dispose` raised none of its own over it.
    let recordThenUnwind (make: unit -> RecordReplayRunner) (complete: bool) : Task<exn option> =
        task {
            let mutable escaped = None

            try
                use recorder = make ()
                let! _ = (runner recorder).OutputStringAsync(crashedRunCommand, CancellationToken.None)

                if complete then
                    recorder.Complete()

                raise (InvalidOperationException unwindFailure)
            with ex ->
                escaped <- Some ex

            return escaped
        }

    /// Assert that the exception which came out of `recordThenUnwind` is the one the scope threw — a
    /// `Dispose` that threw during the unwind would surface here instead of it.
    let assertUnwoundWith (escaped: exn option) : unit =
        match escaped with
        | Some ex ->
            Assert.That(
                ex.Message,
                Is.EqualTo unwindFailure,
                "Dispose must not raise over the exception that is unwinding the stack"
            )
        | None -> Assert.Fail "the recording scope must have been left by the thrown exception"

    let isWindows = RuntimeInformation.IsOSPlatform OSPlatform.Windows

    /// Drain an async line stream to a list (mirrors the streaming tests' helper).
    let collect (lines: IAsyncEnumerable<string>) : Task<string list> =
        task {
            let acc = ResizeArray<string>()
            let enumerator = lines.GetAsyncEnumerator()
            let mutable more = true

            while more do
                let! has = enumerator.MoveNextAsync()

                if has then acc.Add enumerator.Current else more <- false

            do! enumerator.DisposeAsync()
            return List.ofSeq acc
        }

    /// Record `recorded` (a fixed reply), persist, then strict-replay `probe` — so a test can assert
    /// whether two commands share a replay key by whether the probe replays the recording or misses.
    let recordThenProbe
        (path: string)
        (recorded: Command)
        (probe: Command)
        : Task<Result<ProcessResult<string>, ProcessError>> =
        task {
            do!
                task {
                    use recorder = RecordReplayRunner.Record(path, FixedRunner("recorded-output", 0))
                    let! _ = (runner recorder).OutputStringAsync(recorded, CancellationToken.None)

                    match recorder.Save() with
                    | Ok() -> ()
                    | Error error -> Assert.Fail $"save: {error}"
                }

            match RecordReplayRunner.Replay path with
            | Error error ->
                Assert.Fail $"replay load: {error}"
                return Error error
            | Ok replayer -> return! (runner replayer).OutputStringAsync(probe, CancellationToken.None)
        }

    /// `recordThenProbe` over an explicit inner runner, so the recording half can be made through one
    /// that answers like a real run for the wiring under test (`WiringAwareRunner`) instead of the
    /// fixed reply `recordThenProbe` uses.
    let recordThenProbeVia
        (inner: IProcessRunner)
        (path: string)
        (recorded: Command)
        (probe: Command)
        : Task<Result<ProcessResult<string>, ProcessError>> =
        task {
            do!
                task {
                    use recorder = RecordReplayRunner.Record(path, inner)
                    let! _ = (runner recorder).OutputStringAsync(recorded, CancellationToken.None)

                    match recorder.Save() with
                    | Ok() -> ()
                    | Error error -> Assert.Fail $"save: {error}"
                }

            match RecordReplayRunner.Replay path with
            | Error error ->
                Assert.Fail $"replay load: {error}"
                return Error error
            | Ok replayer -> return! (runner replayer).OutputStringAsync(probe, CancellationToken.None)
        }

    /// A temp path for a `StdoutToFile`/`StderrToFile` redirect a wiring test keys on. Nothing writes
    /// it — a replayed redirect must not pretend the file was produced — so tests assert its absence.
    let redirectPath () =
        Path.Combine(Path.GetTempPath(), $"pk-wiring-{Guid.NewGuid():N}.log")

    [<Test>]
    member _.``replay completion honours an already-cancelled Command.CancelOn``() : Task =
        withCassette (fun path ->
            task {
                do!
                    task {
                        use recorder = RecordReplayRunner.Record(path, FixedRunner("recorded", 0))
                        let! _ = (runner recorder).OutputStringAsync(Command.create "tool", CancellationToken.None)

                        match recorder.Save() with
                        | Ok() -> ()
                        | Error error -> Assert.Fail $"save: {error}"
                    }

                use cancelled = new CancellationTokenSource()
                cancelled.Cancel()

                match RecordReplayRunner.Replay path with
                | Error error -> Assert.Fail $"replay load: {error}"
                | Ok replayer ->
                    let command = (Command.create "tool").CancelOn(cancelled.Token)

                    match! (runner replayer).OutputStringAsync(command, CancellationToken.None) with
                    | Error(ProcessError.Cancelled "tool") -> ()
                    | other -> Assert.Fail $"expected Command.CancelOn cancellation, got {other}"
            })

    [<Test>]
    member _.``replay Start ignores Command.CancelOn after the spawn boundary``() : Task =
        withCassette (fun path ->
            task {
                do!
                    task {
                        use recorder = RecordReplayRunner.Record(path, FixedRunner("recorded", 0))
                        let! _ = (runner recorder).OutputStringAsync(Command.create "tool", CancellationToken.None)

                        match recorder.Save() with
                        | Ok() -> ()
                        | Error error -> Assert.Fail $"save: {error}"
                    }

                use cancelled = new CancellationTokenSource()
                cancelled.Cancel()

                match RecordReplayRunner.Replay path with
                | Error error -> Assert.Fail $"replay load: {error}"
                | Ok replayer ->
                    let command = (Command.create "tool").CancelOn(cancelled.Token)

                    match! (runner replayer).StartAsync(command, CancellationToken.None) with
                    | Error error -> Assert.Fail $"CancelOn must not cancel a caller-owned live handle: {error}"
                    | Ok running ->
                        use running = running

                        match! running.OutputStringAsync() with
                        | Ok result -> Assert.That(result.Stdout, Is.EqualTo "recorded")
                        | Error error -> Assert.Fail $"replayed fake failed: {error}"
            })

    [<Test>]
    member _.``a cassette entry with omitted fields replays without a NullReferenceException``() : Task =
        withCassette (fun path ->
            task {
                // A hand-crafted / partially-written cassette: the entry omits Stdout, Stderr, Cwd, and
                // the codes. Loading must normalize the nulls so replay yields "" rather than NRE-ing
                // when a consumer calls e.g. Stdout.TrimEnd.
                File.WriteAllText(
                    path,
                    """{ "Version": 1, "Entries": [ { "Program": "partial-tool", "Args": ["x"], "HasStdin": false, "EnvNames": [] } ] }"""
                )

                match RecordReplayRunner.Replay path with
                | Error error -> Assert.Fail $"replay load failed: {error}"
                | Ok replayer ->
                    let command = Command.create "partial-tool" |> Command.arg "x"

                    match! (runner replayer).OutputStringAsync(command, CancellationToken.None) with
                    | Error error -> Assert.Fail $"replay failed: {error}"
                    | Ok result ->
                        Assert.That(result.Stdout, Is.EqualTo "")
                        Assert.That(result.Stderr, Is.EqualTo "")
                        // Prove the normalized value is a real (non-null) string.
                        Assert.That(result.Stdout.TrimEnd(), Is.EqualTo "")
            })

    [<Test>]
    member _.``a crafted oversized DurationMs is clamped, not an overflow on replay``() : Task =
        withCassette (fun path ->
            task {
                // A hand-edited cassette with a DurationMs far beyond TimeSpan's range: normalization must
                // clamp it so replay's TimeSpan.FromMilliseconds can't throw OverflowException.
                File.WriteAllText(
                    path,
                    """{ "Version": 1, "Entries": [ { "Program": "tool", "Args": ["x"], "HasStdin": false, "EnvNames": [], "Stdout": "out", "Stderr": "", "DurationMs": 1e18 } ] }"""
                )

                match RecordReplayRunner.Replay path with
                | Error error -> Assert.Fail $"replay load: {error}"
                | Ok replayer ->
                    let command = Command.create "tool" |> Command.arg "x"

                    match! (runner replayer).OutputStringAsync(command, CancellationToken.None) with
                    | Ok result ->
                        Assert.That(result.Stdout, Is.EqualTo "out")
                        Assert.That(result.Duration, Is.LessThanOrEqualTo TimeSpan.MaxValue)
                    | Error error -> Assert.Fail $"replay must not fault on an oversized duration: {error}"
            })

    [<Test>]
    member _.``a cassette with an unsupported format version is rejected``() : Task =
        withCassette (fun path ->
            task {
                File.WriteAllText(path, """{ "Version": 999, "Entries": [] }""")

                match RecordReplayRunner.Replay path with
                | Error _ -> ()
                | Ok _ -> Assert.Fail "expected an unsupported-version cassette to be rejected"
            })

    [<Test>]
    member _.``a saved cassette is owner-only on Unix``() : Task =
        task {
            // A fresh (not pre-created) path, so the mode reflects how the cassette was written, not a
            // pre-existing file's permissions.
            let path = Path.Combine(Path.GetTempPath(), $"pk-cassette-{Guid.NewGuid():N}.json")

            try
                let recorder = RecordReplayRunner.Record(path, FixedRunner("secret-output", 0))
                let command = Command.create "tool" |> Command.arg "x"
                let! _ = (runner recorder).OutputStringAsync(command, CancellationToken.None)

                match recorder.Save() with
                | Error error -> Assert.Fail $"save failed: {error}"
                | Ok() ->
                    Assert.That(File.Exists path, Is.True)

                    if not isWindows then
                        Assert.That(
                            File.GetUnixFileMode path,
                            Is.EqualTo(UnixFileMode.UserRead ||| UnixFileMode.UserWrite)
                        )
            finally
                deleteCassette path
        }

    [<Test>]
    member _.``record then replay round-trips a result without the inner runner``() : Task =
        withCassette (fun path ->
            task {
                let recorder = RecordReplayRunner.Record(path, FixedRunner("recorded-output", 0))
                let command = Command.create "tool" |> Command.args [ "build"; "--fast" ]

                match! (runner recorder).OutputStringAsync(command, CancellationToken.None) with
                | Ok result -> Assert.That(result.Stdout, Is.EqualTo "recorded-output")
                | Error error -> Assert.Fail $"{error}"

                match recorder.Save() with
                | Ok() -> ()
                | Error error -> Assert.Fail $"save: {error}"

                match RecordReplayRunner.Replay path with
                | Error error -> Assert.Fail $"replay load: {error}"
                | Ok replayer ->
                    match! (runner replayer).OutputStringAsync(command, CancellationToken.None) with
                    | Ok result ->
                        Assert.That(result.Stdout, Is.EqualTo "recorded-output")
                        Assert.That(result.Code, Is.EqualTo(Some 0))
                    | Error error -> Assert.Fail $"{error}"
            })

    [<Test>]
    member _.``an unrecorded invocation is a CassetteMiss``() : Task =
        withCassette (fun path ->
            task {
                let recorder = RecordReplayRunner.Record(path, FixedRunner("out", 0))
                let recorded = Command.create "tool" |> Command.arg "x"
                let! _ = (runner recorder).OutputStringAsync(recorded, CancellationToken.None)
                recorder.Save() |> ignore

                match RecordReplayRunner.Replay path with
                | Error error -> Assert.Fail $"{error}"
                | Ok replayer ->
                    let unseen = Command.create "tool" |> Command.arg "y"

                    match! (runner replayer).OutputStringAsync(unseen, CancellationToken.None) with
                    | Error(ProcessError.CassetteMiss "tool") -> Assert.Pass()
                    | other -> Assert.Fail $"expected CassetteMiss, got {other}"
            })

    [<Test>]
    member _.``Windows raw fragments are opaque cassette match tokens``() : Task =
        withCassette (fun path ->
            task {
                let raw = "PROP=\"value with spaces\""
                let recorder = RecordReplayRunner.Record(path, FixedRunner("recorded", 0))

                let recorded =
                    Command.create "tool" |> Command.arg "install" |> Command.windowsRawArg raw

                let! _ = (runner recorder).OutputStringAsync(recorded, CancellationToken.None)
                recorder.Save() |> ignore

                match RecordReplayRunner.Replay path with
                | Error error -> Assert.Fail $"replay load: {error}"
                | Ok replayer ->
                    let same =
                        Command.create "tool" |> Command.arg "install" |> Command.windowsRawArg raw

                    match! (runner replayer).OutputStringAsync(same, CancellationToken.None) with
                    | Error error -> Assert.Fail $"same raw fragment should replay: {error}"
                    | Ok result -> Assert.That(result.Stdout, Is.EqualTo "recorded")

                    let different =
                        Command.create "tool"
                        |> Command.arg "install"
                        |> Command.windowsRawArg "PROP=other"

                    match! (runner replayer).OutputStringAsync(different, CancellationToken.None) with
                    | Error(ProcessError.CassetteMiss _) -> ()
                    | other -> Assert.Fail $"a different raw fragment should miss, got {other}"
            })

    [<Test>]
    member _.``the stdin source is part of the match key``() : Task =
        withCassette (fun path ->
            task {
                let recorder = RecordReplayRunner.Record(path, FixedRunner("with-input", 0))
                let recorded = Command.create "tool" |> Command.stdin (Stdin.FromString "input-a")
                let! _ = (runner recorder).OutputStringAsync(recorded, CancellationToken.None)
                recorder.Save() |> ignore

                match RecordReplayRunner.Replay path with
                | Error error -> Assert.Fail $"{error}"
                | Ok replayer ->
                    // Same stdin matches.
                    let same = Command.create "tool" |> Command.stdin (Stdin.FromString "input-a")

                    match! (runner replayer).OutputStringAsync(same, CancellationToken.None) with
                    | Ok result -> Assert.That(result.Stdout, Is.EqualTo "with-input")
                    | Error error -> Assert.Fail $"same stdin should match: {error}"

                    // Different stdin misses.
                    let different = Command.create "tool" |> Command.stdin (Stdin.FromString "input-b")

                    match! (runner replayer).OutputStringAsync(different, CancellationToken.None) with
                    | Error(ProcessError.CassetteMiss _) -> Assert.Pass()
                    | other -> Assert.Fail $"different stdin should miss, got {other}"
            })

    [<Test>]
    member _.``an inherited-stdin command records and strict-replays``() : Task =
        withCassette (fun path ->
            task {
                // InheritStdin is a keyable, repeatable source (unlike the one-shot streaming sources it
                // is NOT rejected at record time): recording spawns for real through the inner runner, and
                // the same inherited-stdin command replays the recording by its stable "inherit" key.
                let inheritCmd () =
                    Command.create "prompt-tool" |> Command.inheritStdin

                match! recordThenProbe path (inheritCmd ()) (inheritCmd ()) with
                | Ok result -> Assert.That(result.Stdout, Is.EqualTo "recorded-output")
                | Error error -> Assert.Fail $"inherited-stdin replay should match: {error}"
            })

    [<Test>]
    member _.``an inherited-stdin command keys distinctly from a no-stdin command``() : Task =
        withCassette (fun path ->
            task {
                // Inherited stdin must not collapse onto the no-stdin key: a recording made with
                // InheritStdin must NOT replay for the same command run without it (and vice versa).
                let inheritCmd = Command.create "prompt-tool" |> Command.inheritStdin
                let plainCmd = Command.create "prompt-tool"

                match! recordThenProbe path inheritCmd plainCmd with
                | Error(ProcessError.CassetteMiss _) -> Assert.Pass()
                | other -> Assert.Fail $"a no-stdin probe of an inherited-stdin recording should miss, got {other}"
            })

    [<Test>]
    member _.``inherited stdin never aliases text that was the legacy inherit sentinel``() : Task =
        withCassette (fun path ->
            task {
                let text =
                    Command.create "prompt-tool" |> Command.stdin (Stdin.FromString "inherit-stdin")

                let inherited = Command.create "prompt-tool" |> Command.inheritStdin

                match! recordThenProbe path text inherited with
                | Error(ProcessError.CassetteMiss _) -> Assert.Pass()
                | other -> Assert.Fail $"inherited stdin must not replay an in-memory sentinel string, got {other}"
            })

    [<Test>]
    member _.``path-only file stdin never aliases text that was the legacy path sentinel``() : Task =
        withCassette (fun path ->
            task {
                let text = Command.create "tool" |> Command.stdin (Stdin.FromString "file:/x")
                let file = Command.create "tool" |> Command.stdin (Stdin.FromFile "/x")

                match! recordThenProbe path text file with
                | Error(ProcessError.CassetteMiss _) -> Assert.Pass()
                | other -> Assert.Fail $"path-only file stdin must not replay an in-memory path string, got {other}"
            })

    [<Test>]
    member _.``by default cwd does not participate in the match key``() : Task =
        withCassette (fun path ->
            task {
                let recorded =
                    Command.create "tool" |> Command.currentDir "/one/dir" |> Command.arg "x"

                let probe =
                    Command.create "tool" |> Command.currentDir "/another/dir" |> Command.arg "x"

                match! recordThenProbe path recorded probe with
                | Ok result -> Assert.That(result.Stdout, Is.EqualTo "recorded-output")
                | Error error -> Assert.Fail $"a different cwd should still match by default: {error}"
            })

    [<Test>]
    member _.``WithCwdMatching() restores cwd as part of the match key``() : Task =
        withCassette (fun path ->
            task {
                let options = RecordReplayOptions().WithCwdMatching()

                let recorded =
                    Command.create "tool" |> Command.currentDir "/one/dir" |> Command.arg "x"

                do!
                    task {
                        use recorder =
                            RecordReplayRunner.Record(path, FixedRunner("recorded-output", 0), options)

                        let! _ = (runner recorder).OutputStringAsync(recorded, CancellationToken.None)

                        match recorder.Save() with
                        | Ok() -> ()
                        | Error error -> Assert.Fail $"save: {error}"
                    }

                match RecordReplayRunner.Replay(path, options) with
                | Error error -> Assert.Fail $"replay load: {error}"
                | Ok replayer ->
                    // Same cwd still matches.
                    let same = Command.create "tool" |> Command.currentDir "/one/dir" |> Command.arg "x"

                    match! (runner replayer).OutputStringAsync(same, CancellationToken.None) with
                    | Ok result -> Assert.That(result.Stdout, Is.EqualTo "recorded-output")
                    | Error error -> Assert.Fail $"same cwd should match: {error}"

                    // A different cwd now misses.
                    let different =
                        Command.create "tool" |> Command.currentDir "/another/dir" |> Command.arg "x"

                    match! (runner replayer).OutputStringAsync(different, CancellationToken.None) with
                    | Error(ProcessError.CassetteMiss _) -> Assert.Pass()
                    | other -> Assert.Fail $"a different cwd should miss with WithCwdMatching(), got {other}"
            })

    [<Test>]
    member _.``a one-shot stdin source cannot be keyed``() : Task =
        withCassette (fun path ->
            task {
                let recorder = RecordReplayRunner.Record(path, FixedRunner("out", 0))
                use reader = new MemoryStream(Encoding.UTF8.GetBytes "data")
                let command = Command.create "tool" |> Command.stdin (Stdin.FromStream reader)

                match! (runner recorder).OutputStringAsync(command, CancellationToken.None) with
                | Error(ProcessError.Unsupported _) -> Assert.Pass()
                | other -> Assert.Fail $"expected Unsupported for a one-shot stdin source, got {other}"
            })

    [<Test>]
    member _.``Dispose flushes a completed recording; the bytes verb is rejected``() : Task =
        withCassette (fun path ->
            task {
                let command = Command.create "tool" |> Command.arg "z"

                // No explicit Save — the drop-time flush of a recording declared finished must persist it.
                do!
                    task {
                        use recorder = RecordReplayRunner.Record(path, FixedRunner("byte-output", 0))
                        let! _ = (runner recorder).OutputStringAsync(command, CancellationToken.None)
                        recorder.Complete()
                    }

                match RecordReplayRunner.Replay path with
                | Error error -> Assert.Fail $"{error}"
                | Ok replayer ->
                    // The dispose-time flush persisted the recording: the string verb replays it.
                    match! (runner replayer).OutputStringAsync(command, CancellationToken.None) with
                    | Ok result -> Assert.That(result.Stdout, Is.EqualTo "byte-output")
                    | Error error -> Assert.Fail $"string replay: {error}"

                    // A cassette stores text, not exact bytes, so the bytes verb is rejected rather than
                    // returning a lossy UTF-8 round-trip.
                    match! (runner replayer).OutputBytesAsync(command, CancellationToken.None) with
                    | Error(ProcessError.Unsupported _) -> ()
                    | other -> Assert.Fail $"expected the bytes verb to be Unsupported, got {other}"
            })

    // --- A dispose reached by an exceptional unwind persists nothing (T-355) -----------------------
    //
    // `Dispose` runs both on a normal scope exit and while the stack unwinds out of a failed assertion,
    // and .NET tells it nothing about which one it is — so the drop-time flush is gated on `Complete`,
    // the caller's own statement that the recording finished as intended. Every test below is run for
    // both recording modes, because `Record` and `Auto` share that one flush path.

    [<TestCase("Record")>]
    [<TestCase("Auto")>]
    member _.``a recorder disposed while an exception unwinds writes no cassette``(mode: string) : Task =
        withUnwrittenCassette (fun path ->
            task {
                let! escaped =
                    recordThenUnwind
                        (fun () -> recorderFor mode path (FixedRunner(crashedRunOutput, 0)))
                        leftIncomplete

                assertUnwoundWith escaped

                Assert.That(
                    File.Exists path,
                    Is.False,
                    $"a crash before Complete must leave no cassette holding the recorded argv/output ({mode})"
                )

                Assert.That(
                    File.Exists(path + ".lock"),
                    Is.False,
                    $"the flush path must not be entered at all, so its sibling lock file is never created ({mode})"
                )
            })

    [<TestCase("Record")>]
    [<TestCase("Auto")>]
    member _.``a recorder disposed while an exception unwinds leaves an existing cassette byte for byte``
        (mode: string)
        : Task =
        withCassette (fun path ->
            task {
                // A cassette on disk, saved the honest way, that the crashed run below must not touch.
                do!
                    task {
                        use seeded = RecordReplayRunner.Record(path, FixedRunner("committed-output", 0))
                        do! recordEntries seeded "committed" 1

                        match seeded.Save() with
                        | Ok() -> ()
                        | Error error -> Assert.Fail $"seed save: {error}"
                    }

                let before = File.ReadAllBytes path

                let! escaped =
                    recordThenUnwind
                        (fun () -> recorderFor mode path (FixedRunner(crashedRunOutput, 0)))
                        leftIncomplete

                assertUnwoundWith escaped

                CollectionAssert.AreEqual(
                    before,
                    File.ReadAllBytes path,
                    $"a crash before Complete must not rewrite, grow, or truncate the saved cassette ({mode})"
                )
            })

    [<TestCase("Record")>]
    [<TestCase("Auto")>]
    member _.``a recording completed before the throw is flushed by that same unwinding dispose``(mode: string) : Task =
        withUnwrittenCassette (fun path ->
            task {
                let! escaped =
                    recordThenUnwind
                        (fun () -> recorderFor mode path (FixedRunner(crashedRunOutput, 0)))
                        declaredComplete

                assertUnwoundWith escaped

                // The same scope, the same throw, the same `Dispose` — only the completion mark differs.
                // That is what makes the two tests above evidence of a flush that was REFUSED rather than
                // one that never ran, and it is the documented contract: `Complete` says the recording is
                // finished, so a later failure no longer suppresses it.
                Assert.That(
                    File.Exists path,
                    Is.True,
                    $"a completed recording must still be flushed on dispose ({mode})"
                )

                CollectionAssert.AreEqual([| crashedRunCommand.Program |], cassettePrograms path)
            })

    [<TestCase("Record")>]
    [<TestCase("Auto")>]
    member _.``a recorder disposed without Complete writes nothing on a normal exit either``(mode: string) : Task =
        withUnwrittenCassette (fun path ->
            task {
                // The completion mark, not the shape of the exit, is the whole gate — so a scope that ends
                // NORMALLY is refused the flush exactly as an unwinding one is. That is what makes the
                // documented alternative real: a caller who wants no write they cannot point at leaves
                // `Complete` out altogether and `Save`s where the file should appear.
                do!
                    task {
                        use recorder = recorderFor mode path (FixedRunner(crashedRunOutput, 0))
                        do! recordEntries recorder "uncompleted" 1
                    }

                Assert.That(
                    File.Exists path,
                    Is.False,
                    $"a dispose without Complete must write no cassette even on a normal scope exit ({mode})"
                )
            })

    [<TestCase("Record")>]
    [<TestCase("Auto")>]
    member _.``an explicit Save persists without Complete and still reports a write failure``(mode: string) : Task =
        withUnwrittenCassette (fun path ->
            task {
                do!
                    task {
                        use recorder = recorderFor mode path (FixedRunner("saved-output", 0))
                        do! recordEntries recorder "saved" 1

                        // No Complete anywhere: an explicit save is unconditional, which is what keeps it
                        // the one durability path however the surrounding scope ends.
                        match recorder.Save() with
                        | Ok() -> ()
                        | Error error -> Assert.Fail $"save: {error}"
                    }

                CollectionAssert.AreEqual(
                    [| "saved" |],
                    cassettePrograms path,
                    $"an explicit Save must write without Complete ({mode})"
                )

                // ...and a save that cannot write still reports that as itself.
                let unwritable = Path.Combine(path + "-missing-directory", "cassette.json")
                use blocked = recorderFor mode unwritable (FixedRunner("saved-output", 0))
                do! recordEntries blocked "saved" 1

                match blocked.Save() with
                | Ok() -> Assert.Fail "a save into a missing directory must not report success"
                | Error(ProcessError.Io _) -> ()
                | Error other -> Assert.Fail $"expected a typed I/O failure, got {other}"
            })

    [<Test>]
    member _.``Complete on a replay-mode recorder neither throws nor touches the cassette``() : Task =
        withCassette (fun path ->
            task {
                let command = Command.create "tool" |> Command.arg "z"

                do!
                    task {
                        use seeded = RecordReplayRunner.Record(path, FixedRunner("replayed-output", 0))
                        let! _ = (runner seeded).OutputStringAsync(command, CancellationToken.None)

                        match seeded.Save() with
                        | Ok() -> ()
                        | Error error -> Assert.Fail $"seed save: {error}"
                    }

                let before = File.ReadAllBytes path

                match RecordReplayRunner.Replay path with
                | Error error -> Assert.Fail $"replay load: {error}"
                | Ok replayer ->
                    do!
                        task {
                            use replayer = replayer
                            let! _ = (runner replayer).OutputStringAsync(command, CancellationToken.None)
                            // Replay records nothing, so there is nothing for a completed scope to flush.
                            replayer.Complete()
                        }

                    CollectionAssert.AreEqual(
                        before,
                        File.ReadAllBytes path,
                        "a replay session must never write to the cassette it is replaying"
                    )
            })

    [<Test>]
    member _.``replay preserves the recorded Truncated flag and Duration``() : Task =
        withCassette (fun path ->
            task {
                let command = Command.create "tool" |> Command.arg "z"
                let recordedDuration = TimeSpan.FromMilliseconds 250.0

                // An inner runner whose captured result was truncated and took a measurable duration —
                // both must survive record + replay (previously replay reported false / 0).
                let inner =
                    { new IProcessRunner with
                        member _.CaptureStringAsync(cmd, _ct) =
                            Task.FromResult(
                                Ok(
                                    ProcessResult<string>(
                                        cmd.Program,
                                        "clipped",
                                        "",
                                        Outcome.Exited 0,
                                        recordedDuration,
                                        true,
                                        [ 0 ]
                                    )
                                )
                            )

                        member _.CaptureBytesAsync(_cmd, _ct) =
                            Task.FromResult(Error(ProcessError.Unsupported "n/a"))

                        member _.SpawnAsync(_cmd, _ct) =
                            Task.FromResult(Error(ProcessError.Unsupported "n/a")) }

                do!
                    task {
                        use recorder = RecordReplayRunner.Record(path, inner)
                        let! _ = (runner recorder).OutputStringAsync(command, CancellationToken.None)
                        recorder.Complete()
                    }

                match RecordReplayRunner.Replay path with
                | Error error -> Assert.Fail $"{error}"
                | Ok replayer ->
                    match! (runner replayer).OutputStringAsync(command, CancellationToken.None) with
                    | Ok result ->
                        Assert.That(result.Truncated, Is.True, "recorded Truncated must survive replay")

                        Assert.That(
                            result.Duration,
                            Is.EqualTo recordedDuration,
                            "recorded Duration must survive replay"
                        )
                    | Error error -> Assert.Fail $"{error}"
            })

    [<Test>]
    member _.``SpawnAsync replay preserves recorded text completion metadata for buffered and streaming consumers``
        ()
        : Task =
        withCassette (fun path ->
            task {
                let command = Command.create "metadata-text" |> Command.arg "replay"
                let recordedDuration = TimeSpan.FromMilliseconds 250.0
                let inner = FixedRunner("line1\nline2", 3, recordedDuration, true)

                do!
                    task {
                        use recorder = RecordReplayRunner.Record(path, inner)
                        let! _ = (runner recorder).OutputStringAsync(command, CancellationToken.None)
                        recorder.Complete()
                    }

                match RecordReplayRunner.Replay path with
                | Error error -> Assert.Fail $"{error}"
                | Ok replayer ->
                    let replay = runner replayer

                    let! direct = replay.OutputStringAsync(command, CancellationToken.None)

                    let directResult =
                        match direct with
                        | Ok result -> result
                        | Error error ->
                            Assert.Fail $"direct text replay failed: {error}"
                            Unchecked.defaultof<_>

                    match! replay.SpawnAsync(command, CancellationToken.None) with
                    | Error error -> Assert.Fail $"spawned text replay failed: {error}"
                    | Ok spawned ->
                        use running = spawned

                        match! running.OutputStringAsync() with
                        | Error error -> Assert.Fail $"spawned OutputStringAsync failed: {error}"
                        | Ok result ->
                            Assert.That(result.Outcome, Is.EqualTo directResult.Outcome)
                            Assert.That(result.Duration, Is.EqualTo directResult.Duration)
                            Assert.That(result.Truncated, Is.EqualTo directResult.Truncated)

                    match! replay.SpawnAsync(command, CancellationToken.None) with
                    | Error error -> Assert.Fail $"streaming replay failed: {error}"
                    | Ok spawned ->
                        use running = spawned
                        let! lines = collect (running.StdoutLinesAsync())
                        CollectionAssert.AreEqual([| "line1"; "line2" |], List.toArray lines)

                        match! running.FinishAsync() with
                        | Error error -> Assert.Fail $"streaming FinishAsync failed: {error}"
                        | Ok finished ->
                            Assert.That(finished.Outcome, Is.EqualTo directResult.Outcome)
                            Assert.That(finished.Truncated, Is.EqualTo directResult.Truncated)
                            Assert.That(running.Elapsed, Is.EqualTo directResult.Duration)
            })

    [<Test>]
    member _.``SpawnAsync replay preserves recorded bytes completion metadata``() : Task =
        withCassette (fun path ->
            task {
                let payload = Encoding.UTF8.GetBytes "byte payload"
                let command = Command.create "metadata-bytes" |> Command.arg "replay"
                let recordedDuration = TimeSpan.FromMilliseconds 250.0
                let inner = FixedBytesRunner(payload, "warning", 0, recordedDuration, true)

                do!
                    task {
                        use recorder = RecordReplayRunner.Record(path, inner)
                        let! _ = (runner recorder).OutputBytesAsync(command, CancellationToken.None)
                        recorder.Complete()
                    }

                match RecordReplayRunner.Replay path with
                | Error error -> Assert.Fail $"{error}"
                | Ok replayer ->
                    let replay = runner replayer
                    let! direct = replay.OutputBytesAsync(command, CancellationToken.None)

                    let directResult =
                        match direct with
                        | Ok result -> result
                        | Error error ->
                            Assert.Fail $"direct bytes replay failed: {error}"
                            Unchecked.defaultof<_>

                    match! replay.SpawnAsync(command, CancellationToken.None) with
                    | Error error -> Assert.Fail $"spawned bytes replay failed: {error}"
                    | Ok spawned ->
                        use running = spawned

                        match! running.OutputBytesAsync() with
                        | Error error -> Assert.Fail $"spawned OutputBytesAsync failed: {error}"
                        | Ok result ->
                            CollectionAssert.AreEqual(directResult.Stdout, result.Stdout)
                            Assert.That(result.Outcome, Is.EqualTo directResult.Outcome)
                            Assert.That(result.Duration, Is.EqualTo directResult.Duration)
                            Assert.That(result.Truncated, Is.EqualTo directResult.Truncated)
            })

    [<Test>]
    member _.``SpawnAsync replay ORs recorded truncation with the current output policy``() : Task =
        withCassette (fun path ->
            task {
                let command = Command.create "metadata-policy"

                do!
                    task {
                        use recorder = RecordReplayRunner.Record(path, FixedRunner("0123456789", 0))
                        let! _ = (runner recorder).OutputStringAsync(command, CancellationToken.None)
                        recorder.Complete()
                    }

                match RecordReplayRunner.Replay path with
                | Error error -> Assert.Fail $"{error}"
                | Ok replayer ->
                    let replay = runner replayer

                    match! replay.SpawnAsync(command, CancellationToken.None) with
                    | Error error -> Assert.Fail $"unbounded spawn replay failed: {error}"
                    | Ok spawned ->
                        use running = spawned

                        match! running.OutputStringAsync() with
                        | Ok result -> Assert.That(result.Truncated, Is.False)
                        | Error error -> Assert.Fail $"unbounded replay failed: {error}"

                    let stricterCommand =
                        command |> Command.outputBuffer (OutputBufferPolicy.Unbounded.WithMaxBytes 4)

                    match! replay.SpawnAsync(stricterCommand, CancellationToken.None) with
                    | Error error -> Assert.Fail $"bounded spawn replay failed: {error}"
                    | Ok spawned ->
                        use running = spawned

                        match! running.OutputStringAsync() with
                        | Ok result -> Assert.That(result.Truncated, Is.True)
                        | Error error -> Assert.Fail $"bounded replay failed: {error}"
            })

    [<Test>]
    member _.``PTY SpawnAsync replay preserves recorded completion metadata``() : Task =
        withCassette (fun path ->
            task {
                let command = Command.create "metadata-pty" |> Command.pty
                let recordedDuration = TimeSpan.FromMilliseconds 250.0

                do!
                    task {
                        use recorder =
                            RecordReplayRunner.Record(path, FixedRunner("terminal output", 0, recordedDuration, true))

                        let! _ = (runner recorder).OutputStringAsync(command, CancellationToken.None)
                        recorder.Complete()
                    }

                match RecordReplayRunner.Replay path with
                | Error error -> Assert.Fail $"{error}"
                | Ok replayer ->
                    match! (runner replayer).SpawnAsync(command, CancellationToken.None) with
                    | Error error -> Assert.Fail $"PTY spawn replay failed: {error}"
                    | Ok spawned ->
                        use running = spawned

                        match! running.OutputStringAsync() with
                        | Error error -> Assert.Fail $"PTY OutputStringAsync failed: {error}"
                        | Ok result ->
                            Assert.That(result.Duration, Is.EqualTo recordedDuration)
                            Assert.That(result.Truncated, Is.True)
                            Assert.That(result.Outcome, Is.EqualTo(Outcome.Exited 0))
            })

    [<Test>]
    member _.``record then replay a bytes capture reproduces exact non-UTF-8 bytes``() : Task =
        withCassette (fun path ->
            task {
                // Bytes that are NOT valid UTF-8 (a lone 0xFF, an embedded NUL) — a text cassette would
                // corrupt these; the base64 v2 form must round-trip them exactly.
                let raw = [| 0xFFuy; 0xFEuy; 0x00uy; 0x01uy; 0x80uy; 0x41uy |]

                do!
                    task {
                        use recorder = RecordReplayRunner.Record(path, FixedBytesRunner(raw, "warn", 3))
                        let command = Command.create "tool" |> Command.arg "b" |> Command.okCodes [ 0; 3 ]
                        let! _ = (runner recorder).OutputBytesAsync(command, CancellationToken.None)
                        recorder.Complete()
                    }

                match RecordReplayRunner.Replay path with
                | Error error -> Assert.Fail $"{error}"
                | Ok replayer ->
                    let command = Command.create "tool" |> Command.arg "b" |> Command.okCodes [ 0; 3 ]

                    match! (runner replayer).OutputBytesAsync(command, CancellationToken.None) with
                    | Ok result ->
                        Assert.That(result.Stdout, Is.EqualTo<byte>(raw), "exact bytes must survive record + replay")
                        Assert.That(result.Stderr, Is.EqualTo "warn")
                        Assert.That(result.Code, Is.EqualTo(Some 3))
                    | Error error -> Assert.Fail $"bytes replay failed: {error}"
            })

    [<Test>]
    member _.``a bytes cassette replay preserves configured stdout decoding in text projections``() : Task =
        withCassette (fun path ->
            task {
                // A Latin-1 byte capture is deliberately not valid UTF-8: replay must carry the command's
                // encoding into ProcessResult so both its text helpers and Exit error keep agreeing with live.
                let text = "café"
                let command = Command.create "tool" |> Command.stdoutEncoding Encoding.Latin1
                let expectedCombined = text + "\nwarning"

                let assertTextProjections (source: string) (result: ProcessResult<byte[]>) =
                    Assert.That(result.Combined, Is.EqualTo expectedCombined, $"{source} Combined")
                    Assert.That(result.OutputContainsAny [ "CAFÉ" ], Is.True, $"{source} OutputContainsAny")

                    match result.EnsureSuccess() with
                    | Error(ProcessError.Exit(_, 7, stdout, stderr)) ->
                        Assert.That(stdout, Is.EqualTo text, $"{source} Exit stdout")
                        Assert.That(stderr, Is.EqualTo "warning", $"{source} Exit stderr")
                    | other -> Assert.Fail $"{source} must preserve the non-zero Exit error text, got {other}"

                do!
                    task {
                        use recorder =
                            RecordReplayRunner.Record(
                                path,
                                FixedBytesRunner(Encoding.Latin1.GetBytes text, "warning", 7)
                            )

                        match! (runner recorder).OutputBytesAsync(command, CancellationToken.None) with
                        | Ok result -> assertTextProjections "live recording" result
                        | Error error -> Assert.Fail $"live recording failed: {error}"

                        recorder.Complete()
                    }

                match RecordReplayRunner.Replay path with
                | Error error -> Assert.Fail $"replay load: {error}"
                | Ok replayer ->
                    match! (runner replayer).OutputBytesAsync(command, CancellationToken.None) with
                    | Ok result -> assertTextProjections "replay" result
                    | Error error -> Assert.Fail $"replay failed: {error}"
            })

    [<Test>]
    member _.``a bytes cassette replay keeps UTF-8 text projections by default``() : Task =
        withCassette (fun path ->
            task {
                let text = "café"
                let command = Command.create "tool"

                do!
                    task {
                        use recorder =
                            RecordReplayRunner.Record(path, FixedBytesRunner(Encoding.UTF8.GetBytes text, "", 0))

                        let! _ = (runner recorder).OutputBytesAsync(command, CancellationToken.None)
                        recorder.Complete()
                    }

                match RecordReplayRunner.Replay path with
                | Error error -> Assert.Fail $"replay load: {error}"
                | Ok replayer ->
                    match! (runner replayer).OutputBytesAsync(command, CancellationToken.None) with
                    | Ok result -> Assert.That(result.Combined, Is.EqualTo text)
                    | Error error -> Assert.Fail $"replay failed: {error}"
            })

    [<Test>]
    member _.``an empty bytes recording replays as empty bytes, not Unsupported``() : Task =
        withCassette (fun path ->
            task {
                // Load-bearing distinction: a bytes recording of EMPTY output stores StdoutBase64 = "" (not
                // null), so it must replay as empty bytes — while a *text* recording (StdoutBase64 = null)
                // stays Unsupported for the bytes verb. If normalization ever coalesced StdoutBase64 to "",
                // it would silently turn every text entry into an empty-bytes one; this guards that.
                do!
                    task {
                        use recorder = RecordReplayRunner.Record(path, FixedBytesRunner([||], "", 0))
                        let! _ = (runner recorder).OutputBytesAsync(Command.create "tool", CancellationToken.None)
                        recorder.Complete()
                    }

                match RecordReplayRunner.Replay path with
                | Error error -> Assert.Fail $"{error}"
                | Ok replayer ->
                    match! (runner replayer).OutputBytesAsync(Command.create "tool", CancellationToken.None) with
                    | Ok result ->
                        Assert.That(
                            result.Stdout.Length,
                            Is.EqualTo 0,
                            "empty bytes must replay as empty, not Unsupported"
                        )
                    | Error error -> Assert.Fail $"an empty bytes recording must replay as bytes: {error}"
            })

    [<Test>]
    member _.``a redaction hook scrubs a bytes capture's stderr``() : Task =
        withCassette (fun path ->
            task {
                let options =
                    RecordReplayOptions().WithRedaction(fun s -> s.Replace("SECRET", "***"))

                do!
                    task {
                        use recorder =
                            RecordReplayRunner.Record(
                                path,
                                FixedBytesRunner([| 1uy; 2uy |], "err SECRET here", 0),
                                options
                            )

                        let! _ = (runner recorder).OutputBytesAsync(Command.create "tool", CancellationToken.None)
                        recorder.Complete()
                    }

                Assert.That(File.ReadAllText path, Does.Not.Contain "SECRET")

                match RecordReplayRunner.Replay path with
                | Error error -> Assert.Fail $"{error}"
                | Ok replayer ->
                    match! (runner replayer).OutputBytesAsync(Command.create "tool", CancellationToken.None) with
                    | Ok result -> Assert.That(result.Stderr, Is.EqualTo "err *** here")
                    | Error error -> Assert.Fail $"{error}"
            })

    [<Test>]
    member _.``file-content hashing keys equivalently to FromBytes of the same content``() : Task =
        task {
            let cassette = Path.Combine(Path.GetTempPath(), $"pk-cass-{Guid.NewGuid():N}.json")
            let file = Path.Combine(Path.GetTempPath(), $"pk-in-{Guid.NewGuid():N}.txt")
            File.WriteAllText(file, "payload")
            let options = RecordReplayOptions().WithFileStdinContentHashing()

            try
                // Record with a FromFile stdin (keyed by contents)...
                do!
                    task {
                        use recorder = RecordReplayRunner.Record(cassette, FixedRunner("ok", 0), options)
                        let command = Command.create "tool" |> Command.stdin (Stdin.FromFile file)
                        let! _ = (runner recorder).OutputStringAsync(command, CancellationToken.None)
                        recorder.Complete()
                    }

                match RecordReplayRunner.Replay(cassette, options) with
                | Error error -> Assert.Fail $"{error}"
                | Ok replayer ->
                    // ...a FromBytes of the SAME content matches (both hash the content).
                    let viaBytes =
                        Command.create "tool"
                        |> Command.stdin (Stdin.FromBytes(Encoding.UTF8.GetBytes "payload"))

                    match! (runner replayer).OutputStringAsync(viaBytes, CancellationToken.None) with
                    | Ok result -> Assert.That(result.Stdout, Is.EqualTo "ok")
                    | Error error ->
                        Assert.Fail $"FromBytes of the same content should match a content-hashed FromFile: {error}"
            finally
                for f in [ cassette; file ] do
                    if File.Exists f then
                        File.Delete f
        }

    [<Test>]
    member _.``file-content hashing surfaces ProcessError.Stdin for an unreadable stdin file``() : Task =
        withCassette (fun path ->
            task {
                // A cassette to replay against (contents irrelevant — the digest fails first).
                File.WriteAllText(
                    path,
                    """{ "Version": 2, "Entries": [ { "Program": "tool", "Args": [], "HasStdin": true, "EnvNames": [], "Stdout": "x", "Stderr": "" } ] }"""
                )

                let options = RecordReplayOptions().WithFileStdinContentHashing()

                match RecordReplayRunner.Replay(path, options) with
                | Error error -> Assert.Fail $"{error}"
                | Ok replayer ->
                    let missing = Path.Combine(Path.GetTempPath(), $"pk-nope-{Guid.NewGuid():N}.txt")
                    let command = Command.create "tool" |> Command.stdin (Stdin.FromFile missing)

                    match! (runner replayer).OutputStringAsync(command, CancellationToken.None) with
                    | Error(ProcessError.Stdin _) -> Assert.Pass()
                    | other ->
                        Assert.Fail
                            $"an unreadable content-hashed stdin file should surface ProcessError.Stdin, got {other}"
            })

    [<Test>]
    member _.``a bytes recording is also readable through the string verb (decoded)``() : Task =
        withCassette (fun path ->
            task {
                // UTF-8 bytes for "héllo" — a string-verb replay of a bytes recording decodes the base64
                // with the command's stdout encoding, so both verbs read the same entry.
                let raw = Encoding.UTF8.GetBytes "héllo"

                do!
                    task {
                        use recorder = RecordReplayRunner.Record(path, FixedBytesRunner(raw, "", 0))
                        let! _ = (runner recorder).OutputBytesAsync(Command.create "tool", CancellationToken.None)
                        recorder.Complete()
                    }

                match RecordReplayRunner.Replay path with
                | Error error -> Assert.Fail $"{error}"
                | Ok replayer ->
                    match! (runner replayer).OutputStringAsync(Command.create "tool", CancellationToken.None) with
                    | Ok result -> Assert.That(result.Stdout, Is.EqualTo "héllo")
                    | Error error -> Assert.Fail $"string replay of a bytes recording failed: {error}"
            })

    [<Test>]
    member _.``a v1 text cassette still loads, and its bytes verb stays Unsupported``() : Task =
        withCassette (fun path ->
            task {
                // A hand-written v1 cassette (pre-base64). It must still load under the v2 build, its string
                // verb replays the text, and its bytes verb is honestly Unsupported (no exact bytes stored).
                File.WriteAllText(
                    path,
                    """{ "Version": 1, "Entries": [ { "Program": "legacy", "Args": ["x"], "HasStdin": false, "EnvNames": [], "Stdout": "old", "Stderr": "" } ] }"""
                )

                match RecordReplayRunner.Replay path with
                | Error error -> Assert.Fail $"a v1 cassette must still load: {error}"
                | Ok replayer ->
                    let command = Command.create "legacy" |> Command.arg "x"

                    match! (runner replayer).OutputStringAsync(command, CancellationToken.None) with
                    | Ok result -> Assert.That(result.Stdout, Is.EqualTo "old")
                    | Error error -> Assert.Fail $"v1 string replay: {error}"

                    match! (runner replayer).OutputBytesAsync(command, CancellationToken.None) with
                    | Error(ProcessError.Unsupported _) -> ()
                    | other -> Assert.Fail $"a text-only entry's bytes verb must be Unsupported, got {other}"
            })

    [<Test>]
    member _.``SpawnAsync replays a live handle reconstructed from the recording``() : Task =
        withCassette (fun path ->
            task {
                let command = Command.create "server" |> Command.arg "start"

                // Record a multi-line run through a capture verb...
                do!
                    task {
                        use recorder =
                            RecordReplayRunner.Record(path, FixedRunner("line1\nline2\nline3", 0))

                        let! _ = (runner recorder).OutputStringAsync(command, CancellationToken.None)
                        recorder.Complete()
                    }

                // ...then replay it as a *stream*: SpawnAsync reconstructs a live handle from the cassette.
                match RecordReplayRunner.Replay path with
                | Error error -> Assert.Fail $"{error}"
                | Ok replayer ->
                    match! (runner replayer).SpawnAsync(command, CancellationToken.None) with
                    | Error error -> Assert.Fail $"streaming replay failed: {error}"
                    | Ok running ->
                        let! lines = collect (running.StdoutLinesAsync())
                        let! finished = running.FinishAsync()
                        Assert.That(lines, Does.Contain "line1")
                        Assert.That(lines, Does.Contain "line3")

                        match finished with
                        | Ok result -> Assert.That(result.Outcome, Is.EqualTo(Outcome.Exited 0))
                        | Error error -> Assert.Fail $"finish: {error}"
            })

    // T-366: the byte-exact stderr stream must hold on a REPLAYED handle too — a replay reconstructs
    // its handle through the same `FakeProcess` path a scripted double uses, so `StderrChunksAsync`
    // hands back exactly the recorded stderr rather than diverging from a live run's contract. (A
    // cassette records stderr as text, so what replays byte-for-byte is that recorded text in the
    // command's stderr encoding; `StreamingTests` covers scripting arbitrary bytes into a double.)
    [<Test>]
    member _.``SpawnAsync replay streams the recorded stderr as byte chunks``() : Task =
        withCassette (fun path ->
            task {
                let command = Command.create "server" |> Command.arg "start"
                let recordedStderr = "warn-1\nwarn-2\n"

                do!
                    task {
                        use recorder =
                            RecordReplayRunner.Record(
                                path,
                                FixedBytesRunner(Encoding.UTF8.GetBytes "out\n", recordedStderr, 0)
                            )

                        let! _ = (runner recorder).OutputStringAsync(command, CancellationToken.None)
                        recorder.Complete()
                    }

                match RecordReplayRunner.Replay path with
                | Error error -> Assert.Fail $"{error}"
                | Ok replayer ->
                    match! (runner replayer).SpawnAsync(command, CancellationToken.None) with
                    | Error error -> Assert.Fail $"streaming replay failed: {error}"
                    | Ok running ->
                        use _ = running
                        let received = ResizeArray<byte>()
                        let enumerator = running.StderrChunksAsync().GetAsyncEnumerator()
                        let mutable more = true

                        while more do
                            let! has = enumerator.MoveNextAsync()

                            if has then
                                received.AddRange(enumerator.Current.ToArray())
                            else
                                more <- false

                        do! enumerator.DisposeAsync()

                        CollectionAssert.AreEqual(Encoding.UTF8.GetBytes recordedStderr, received.ToArray())

                        match! running.FinishAsync() with
                        | Ok finished -> Assert.That(finished.Outcome, Is.EqualTo(Outcome.Exited 0))
                        | Error error -> Assert.Fail $"finish: {error}"
            })

    [<Test>]
    member _.``record-mode SpawnAsync is unsupported``() : Task =
        withCassette (fun path ->
            task {
                use recorder = RecordReplayRunner.Record(path, FixedRunner("out", 0))

                match! (runner recorder).SpawnAsync(Command.create "tool", CancellationToken.None) with
                | Error(ProcessError.Unsupported _) -> Assert.Pass()
                | other -> Assert.Fail $"record-mode SpawnAsync must be Unsupported, got {other}"
            })

    [<Test>]
    member _.``a pre-cancelled token makes both capture verbs report Cancelled without touching inner``() : Task =
        withCassette (fun path ->
            task {
                // The text and bytes capture verbs share `CaptureVia`'s cancellation guard; prove neither
                // path lost it to the refactor — a cancelled call must short-circuit to `Cancelled` and
                // never reach `inner` (which would otherwise report a live `Ok` result here).
                use recorder = RecordReplayRunner.Record(path, FixedBytesRunner([| 1uy |], "", 0))
                use cts = new CancellationTokenSource()
                cts.Cancel()
                let command = Command.create "tool"

                match! (runner recorder).CaptureStringAsync(command, cts.Token) with
                | Error(ProcessError.Cancelled "tool") -> ()
                | other -> Assert.Fail $"expected Cancelled from the text verb, got {other}"

                match! (runner recorder).CaptureBytesAsync(command, cts.Token) with
                | Error(ProcessError.Cancelled "tool") -> ()
                | other -> Assert.Fail $"expected Cancelled from the bytes verb, got {other}"
            })

    [<Test>]
    member _.``file-stdin content hashing matches on contents, not path``() : Task =
        task {
            let cassette = Path.Combine(Path.GetTempPath(), $"pk-cass-{Guid.NewGuid():N}.json")
            let fileA = Path.Combine(Path.GetTempPath(), $"pk-in-a-{Guid.NewGuid():N}.txt")
            let fileB = Path.Combine(Path.GetTempPath(), $"pk-in-b-{Guid.NewGuid():N}.txt")
            let fileC = Path.Combine(Path.GetTempPath(), $"pk-in-c-{Guid.NewGuid():N}.txt")
            File.WriteAllText(fileA, "shared-input")
            File.WriteAllText(fileB, "shared-input") // same content, different path
            File.WriteAllText(fileC, "other-input") // different content

            let options = RecordReplayOptions().WithFileStdinContentHashing()

            try
                do!
                    task {
                        use recorder = RecordReplayRunner.Record(cassette, FixedRunner("fed", 0), options)
                        let command = Command.create "tool" |> Command.stdin (Stdin.FromFile fileA)
                        let! _ = (runner recorder).OutputStringAsync(command, CancellationToken.None)
                        recorder.Complete()
                    }

                match RecordReplayRunner.Replay(cassette, options) with
                | Error error -> Assert.Fail $"{error}"
                | Ok replayer ->
                    // A different file with identical contents matches.
                    let same = Command.create "tool" |> Command.stdin (Stdin.FromFile fileB)

                    match! (runner replayer).OutputStringAsync(same, CancellationToken.None) with
                    | Ok result -> Assert.That(result.Stdout, Is.EqualTo "fed")
                    | Error error -> Assert.Fail $"identical file contents should match: {error}"

                    // Different contents miss.
                    let different = Command.create "tool" |> Command.stdin (Stdin.FromFile fileC)

                    match! (runner replayer).OutputStringAsync(different, CancellationToken.None) with
                    | Error(ProcessError.CassetteMiss _) -> ()
                    | other -> Assert.Fail $"different file contents should miss, got {other}"
            finally
                for f in [ cassette; fileA; fileB; fileC ] do
                    if File.Exists f then
                        File.Delete f
        }

    [<Test>]
    member _.``an argument normalizer lets a volatile argument still match``() : Task =
        withCassette (fun path ->
            task {
                // Record with a volatile temp-path argument (default, path stored verbatim).
                do!
                    task {
                        use recorder = RecordReplayRunner.Record(path, FixedRunner("done", 0))

                        let recorded =
                            Command.create "tool" |> Command.args [ "--out"; "/tmp/run-aaa"; "build" ]

                        let! _ = (runner recorder).OutputStringAsync(recorded, CancellationToken.None)
                        recorder.Complete()
                    }

                // Replay with a normalizer that drops any /tmp/ argument, so a different temp path matches.
                let options =
                    RecordReplayOptions()
                        .WithArgNormalizer(fun args -> args |> Array.filter (fun a -> not (a.StartsWith "/tmp/")))

                match RecordReplayRunner.Replay(path, options) with
                | Error error -> Assert.Fail $"{error}"
                | Ok replayer ->
                    let live =
                        Command.create "tool" |> Command.args [ "--out"; "/tmp/run-bbb"; "build" ]

                    match! (runner replayer).OutputStringAsync(live, CancellationToken.None) with
                    | Ok result -> Assert.That(result.Stdout, Is.EqualTo "done")
                    | Error error -> Assert.Fail $"a normalized volatile arg should still match: {error}"
            })

    [<Test>]
    member _.``a redaction hook scrubs a secret from the stored cassette``() : Task =
        withCassette (fun path ->
            task {
                let options =
                    RecordReplayOptions().WithRedaction(fun s -> s.Replace("SECRET123", "[REDACTED]"))

                do!
                    task {
                        use recorder =
                            RecordReplayRunner.Record(path, FixedRunner("auth token=SECRET123 ok", 0), options)

                        let! _ = (runner recorder).OutputStringAsync(Command.create "tool", CancellationToken.None)
                        recorder.Complete()
                    }

                // The secret never reached disk...
                let onDisk = File.ReadAllText path
                Assert.That(onDisk, Does.Not.Contain "SECRET123", "the secret must not be stored")
                Assert.That(onDisk, Does.Contain "[REDACTED]")

                // ...and the scrubbed value is what replays.
                match RecordReplayRunner.Replay path with
                | Error error -> Assert.Fail $"{error}"
                | Ok replayer ->
                    match! (runner replayer).OutputStringAsync(Command.create "tool", CancellationToken.None) with
                    | Ok result -> Assert.That(result.Stdout, Is.EqualTo "auth token=[REDACTED] ok")
                    | Error error -> Assert.Fail $"{error}"
            })

    // --- Persisted command projection (cassette format v9) -------------------------------------------
    //
    // `WithCommandProjection` decides what a recording STORES for program/args. It deliberately does not
    // decide what a call MATCHES on: that is a fingerprint of the invoked command line, taken before the
    // projection runs and stored beside the projected text. These tests pin both halves — the secret
    // never reaches disk, and matching is unmoved by the projection in either direction.

    [<Test>]
    member _.``a command projection keeps a secret argument out of the stored command line``() : Task =
        withCassette (fun path ->
            task {
                let options =
                    RecordReplayOptions()
                        .WithCommandProjection(fun program args ->
                            struct (program, args |> Array.map (fun a -> a.Replace("hunter2", "[REDACTED]"))))

                let secretCommand =
                    Command.create "vault" |> Command.args [ "login"; "--password=hunter2" ]

                do!
                    task {
                        use recorder = RecordReplayRunner.Record(path, FixedRunner("ok", 0), options)
                        let! _ = (runner recorder).OutputStringAsync(secretCommand, CancellationToken.None)
                        recorder.Complete()
                    }

                let onDisk = File.ReadAllText path
                Assert.That(onDisk, Does.Not.Contain "hunter2", "a projected argument must not reach disk")
                Assert.That(onDisk, Does.Contain "--password=[REDACTED]", "the projected text is what is stored")
                Assert.That(onDisk, Does.Contain "\"Version\": 9", "the command fingerprint is a v9 field")

                Assert.That(
                    onDisk,
                    Does.Contain "\"CommandFingerprint\"",
                    "a projected entry carries the key its stored text no longer is"
                )

                // The very call that was recorded — secret argument and all — still replays.
                match RecordReplayRunner.Replay(path, options) with
                | Error error -> Assert.Fail $"a projected cassette must load: {error}"
                | Ok replayer ->
                    match! (runner replayer).OutputStringAsync(secretCommand, CancellationToken.None) with
                    | Ok result -> Assert.That(result.Stdout, Is.EqualTo "ok")
                    | Error error -> Assert.Fail $"a projected recording must replay its own call: {error}"
            })

    [<Test>]
    member _.``a projected cassette replays for a caller that configures no projection``() : Task =
        withCassette (fun path ->
            task {
                // The projection is a WRITE-side policy: the key is the invoked command line either way,
                // so a reader needs no matching option to replay what a projected recorder wrote. A
                // genuinely different call still misses — the projection widens nothing.
                let options =
                    RecordReplayOptions()
                        .WithCommandProjection(fun program args ->
                            struct (program, args |> Array.map (fun _ -> "[REDACTED]")))

                let recorded = Command.create "vault" |> Command.args [ "--token=hunter2" ]

                do!
                    task {
                        use recorder = RecordReplayRunner.Record(path, FixedRunner("ok", 0), options)
                        let! _ = (runner recorder).OutputStringAsync(recorded, CancellationToken.None)
                        recorder.Complete()
                    }

                match RecordReplayRunner.Replay path with
                | Error error -> Assert.Fail $"a projected cassette must load without the projection: {error}"
                | Ok replayer ->
                    match! (runner replayer).OutputStringAsync(recorded, CancellationToken.None) with
                    | Ok result -> Assert.That(result.Stdout, Is.EqualTo "ok")
                    | Error error -> Assert.Fail $"the invoked command line must still key the entry: {error}"

                    let other = Command.create "vault" |> Command.args [ "--token=other" ]

                    match! (runner replayer).OutputStringAsync(other, CancellationToken.None) with
                    | Error(ProcessError.CassetteMiss _) -> ()
                    | other -> Assert.Fail $"a different command line must still miss, got {other}"
            })

    [<Test>]
    member _.``two calls whose projections collide stay two distinct recordings``() : Task =
        withCassette (fun path ->
            task {
                // The security-critical half: a projection that maps two DIFFERENT secrets onto the same
                // stored text must not merge the two calls into one key. Keying on the invoked command
                // line is what keeps each replay honest — a projection can never fabricate a hit.
                let options =
                    RecordReplayOptions()
                        .WithCommandProjection(fun program args ->
                            struct (program, args |> Array.map (fun _ -> "--token=[REDACTED]")))

                let first = Command.create "vault" |> Command.args [ "--token=aaa" ]
                let second = Command.create "vault" |> Command.args [ "--token=bbb" ]

                let perSecret =
                    { new IProcessRunner with
                        member _.CaptureStringAsync(cmd, _ct) =
                            let stdout =
                                if Seq.contains "--token=aaa" cmd.Arguments then
                                    "first"
                                else
                                    "second"

                            Task.FromResult(
                                Ok(
                                    ProcessResult<string>(
                                        cmd.Program,
                                        stdout,
                                        "",
                                        Outcome.Exited 0,
                                        TimeSpan.Zero,
                                        false,
                                        [ 0 ]
                                    )
                                )
                            )

                        member _.CaptureBytesAsync(_cmd, _ct) =
                            Task.FromResult<Result<ProcessResult<byte[]>, ProcessError>>(
                                Error(ProcessError.Unsupported "unused")
                            )

                        member _.SpawnAsync(_cmd, _ct) =
                            Task.FromResult<Result<RunningProcess, ProcessError>>(
                                Error(ProcessError.Unsupported "unused")
                            ) }

                do!
                    task {
                        use recorder = RecordReplayRunner.Record(path, perSecret, options)
                        let! _ = (runner recorder).OutputStringAsync(first, CancellationToken.None)
                        let! _ = (runner recorder).OutputStringAsync(second, CancellationToken.None)
                        recorder.Complete()
                    }

                let onDisk = File.ReadAllText path
                Assert.That(onDisk, Does.Not.Contain "--token=aaa", "neither secret may reach disk")
                Assert.That(onDisk, Does.Not.Contain "--token=bbb")

                match RecordReplayRunner.Replay(path, options) with
                | Error error -> Assert.Fail $"load: {error}"
                | Ok replayer ->
                    for command, expected in [ first, "first"; second, "second" ] do
                        match! (runner replayer).OutputStringAsync(command, CancellationToken.None) with
                        | Ok result ->
                            Assert.That(result.Stdout, Is.EqualTo expected, "each secret replays its own recording")
                        | Error error -> Assert.Fail $"a colliding projection must not merge two calls: {error}"

                    // ...and the placeholder itself is not a command anyone recorded, so asking for it as
                    // a real argument misses rather than picking up whichever entry stored that text.
                    let placeholder = Command.create "vault" |> Command.args [ "--token=[REDACTED]" ]

                    match! (runner replayer).OutputStringAsync(placeholder, CancellationToken.None) with
                    | Error(ProcessError.CassetteMiss _) -> ()
                    | other -> Assert.Fail $"the stored placeholder must not match as a command line, got {other}"
            })

    [<Test>]
    member _.``without a projection the command line is stored as invoked and no fingerprint is written``() : Task =
        withCassette (fun path ->
            task {
                // The default is unchanged, byte for byte: raw program/args on disk, no `CommandFingerprint`
                // field at all — so an entry keeps being re-keyed from its own stored text at load, which
                // is what lets a normalizer be introduced or changed after the recording was made.
                let recorded = Command.create "tool" |> Command.args [ "build"; "--out"; "/tmp/x" ]

                do!
                    task {
                        use recorder = RecordReplayRunner.Record(path, FixedRunner("done", 0))
                        let! _ = (runner recorder).OutputStringAsync(recorded, CancellationToken.None)
                        recorder.Complete()
                    }

                let onDisk = File.ReadAllText path
                Assert.That(onDisk, Does.Contain "\"/tmp/x\"", "the raw arguments are still stored verbatim")

                Assert.That(
                    onDisk,
                    Does.Not.Contain "CommandFingerprint",
                    "an unprojected recording writes no fingerprint field"
                )

                match RecordReplayRunner.Replay path with
                | Error error -> Assert.Fail $"load: {error}"
                | Ok replayer ->
                    match! (runner replayer).OutputStringAsync(recorded, CancellationToken.None) with
                    | Ok result -> Assert.That(result.Stdout, Is.EqualTo "done")
                    | Error error -> Assert.Fail $"an unprojected recording must replay as before: {error}"
            })

    [<Test>]
    member _.``a normalizer still decides matching while a projection decides what is stored``() : Task =
        withCassette (fun path ->
            task {
                // The two hooks are orthogonal by construction: the normalizer is folded into the key
                // (before the projection runs), the projection into the file. Both applied at once, a
                // volatile argument still matches and the secret still never lands.
                let options =
                    RecordReplayOptions()
                        .WithArgNormalizer(fun args -> args |> Array.filter (fun a -> not (a.StartsWith "/tmp/")))
                        .WithCommandProjection(fun program args ->
                            struct (program, args |> Array.map (fun a -> a.Replace("hunter2", "[REDACTED]"))))

                do!
                    task {
                        use recorder = RecordReplayRunner.Record(path, FixedRunner("done", 0), options)

                        let recorded =
                            Command.create "vault"
                            |> Command.args [ "--out"; "/tmp/run-aaa"; "--password=hunter2" ]

                        let! _ = (runner recorder).OutputStringAsync(recorded, CancellationToken.None)
                        recorder.Complete()
                    }

                Assert.That(File.ReadAllText path, Does.Not.Contain "hunter2", "the projection still scrubs")

                match RecordReplayRunner.Replay(path, options) with
                | Error error -> Assert.Fail $"load: {error}"
                | Ok replayer ->
                    let live =
                        Command.create "vault"
                        |> Command.args [ "--out"; "/tmp/run-bbb"; "--password=hunter2" ]

                    match! (runner replayer).OutputStringAsync(live, CancellationToken.None) with
                    | Ok result -> Assert.That(result.Stdout, Is.EqualTo "done")
                    | Error error ->
                        Assert.Fail $"a normalized volatile arg must still match under a projection: {error}"
            })

    [<Test>]
    member _.``a projection covers text, bytes, PTY, and typed-failure recordings alike``() : Task =
        task {
            // Every entry is built from one invocation half, so all four recording shapes must be
            // projected on identical terms — and each must still replay for the call that made it.
            let options =
                RecordReplayOptions()
                    .WithCommandProjection(fun program args ->
                        struct (program, args |> Array.map (fun a -> a.Replace("hunter2", "[REDACTED]"))))

            let secretArgs = [ "login"; "--password=hunter2" ]

            // text
            do!
                withCassette (fun path ->
                    task {
                        let command = Command.create "vault" |> Command.args secretArgs

                        do!
                            task {
                                use recorder = RecordReplayRunner.Record(path, FixedRunner("text", 0), options)
                                let! _ = (runner recorder).OutputStringAsync(command, CancellationToken.None)
                                recorder.Complete()
                            }

                        Assert.That(File.ReadAllText path, Does.Not.Contain "hunter2", "text recording")

                        match RecordReplayRunner.Replay(path, options) with
                        | Error error -> Assert.Fail $"text load: {error}"
                        | Ok replayer ->
                            match! (runner replayer).OutputStringAsync(command, CancellationToken.None) with
                            | Ok result -> Assert.That(result.Stdout, Is.EqualTo "text")
                            | Error error -> Assert.Fail $"a projected text recording must replay: {error}"
                    })

            // bytes
            do!
                withCassette (fun path ->
                    task {
                        let command = Command.create "vault" |> Command.args secretArgs

                        do!
                            task {
                                use recorder =
                                    RecordReplayRunner.Record(
                                        path,
                                        FixedBytesRunner([| 0uy; 1uy; 2uy |], "", 0),
                                        options
                                    )

                                let! _ = (runner recorder).OutputBytesAsync(command, CancellationToken.None)
                                recorder.Complete()
                            }

                        Assert.That(File.ReadAllText path, Does.Not.Contain "hunter2", "bytes recording")

                        match RecordReplayRunner.Replay(path, options) with
                        | Error error -> Assert.Fail $"bytes load: {error}"
                        | Ok replayer ->
                            match! (runner replayer).OutputBytesAsync(command, CancellationToken.None) with
                            | Ok result -> CollectionAssert.AreEqual([| 0uy; 1uy; 2uy |], result.Stdout)
                            | Error error -> Assert.Fail $"a projected bytes recording must replay: {error}"
                    })

            // PTY (a merged-stream recording keys and projects exactly like any other)
            do!
                withCassette (fun path ->
                    task {
                        let command = Command.create "vault" |> Command.args secretArgs |> Command.pty

                        do!
                            task {
                                use recorder = RecordReplayRunner.Record(path, FixedRunner("frame", 0), options)
                                let! _ = (runner recorder).OutputStringAsync(command, CancellationToken.None)
                                recorder.Complete()
                            }

                        let onDisk = File.ReadAllText path
                        Assert.That(onDisk, Does.Not.Contain "hunter2", "PTY recording")
                        Assert.That(onDisk, Does.Contain "\"Pty\": true")

                        match RecordReplayRunner.Replay(path, options) with
                        | Error error -> Assert.Fail $"pty load: {error}"
                        | Ok replayer ->
                            match! (runner replayer).OutputStringAsync(command, CancellationToken.None) with
                            | Ok result -> Assert.That(result.Stdout, Is.EqualTo "frame")
                            | Error error -> Assert.Fail $"a projected PTY recording must replay: {error}"
                    })

            // typed failure
            do!
                withCassette (fun path ->
                    task {
                        let command = Command.create "vault" |> Command.args secretArgs

                        do!
                            task {
                                use recorder =
                                    RecordReplayRunner.Record(
                                        path,
                                        ErrorRunner [ ProcessError.Exit("vault", 3, "", "denied") ],
                                        options
                                    )

                                let! _ = (runner recorder).CaptureStringAsync(command, CancellationToken.None)
                                recorder.Complete()
                            }

                        Assert.That(File.ReadAllText path, Does.Not.Contain "hunter2", "failure recording")

                        match RecordReplayRunner.Replay(path, options) with
                        | Error error -> Assert.Fail $"failure load: {error}"
                        | Ok replayer ->
                            match! (runner replayer).CaptureStringAsync(command, CancellationToken.None) with
                            | Error(ProcessError.Exit(program, 3, _, stderr)) ->
                                Assert.That(program, Is.EqualTo "vault", "a replayed failure names the LIVE program")
                                Assert.That(stderr, Is.EqualTo "denied")
                            | other -> Assert.Fail $"a projected failure must replay as itself, got {other}"
                    })
        }

    [<Test>]
    member _.``Auto grows a projected cassette and replays it, in session and after a reload``() : Task =
        withCassette (fun path ->
            task {
                let options =
                    RecordReplayOptions()
                        .WithCommandProjection(fun program args ->
                            struct (program, args |> Array.map (fun a -> a.Replace("hunter2", "[REDACTED]"))))

                let command = Command.create "vault" |> Command.args [ "--password=hunter2" ]

                let mutable calls = 0

                let counting =
                    { new IProcessRunner with
                        member _.CaptureStringAsync(cmd, _ct) =
                            calls <- calls + 1

                            Task.FromResult(
                                Ok(
                                    ProcessResult<string>(
                                        cmd.Program,
                                        "live",
                                        "",
                                        Outcome.Exited 0,
                                        TimeSpan.Zero,
                                        false,
                                        [ 0 ]
                                    )
                                )
                            )

                        member _.CaptureBytesAsync(_cmd, _ct) =
                            Task.FromResult<Result<ProcessResult<byte[]>, ProcessError>>(
                                Error(ProcessError.Unsupported "unused")
                            )

                        member _.SpawnAsync(_cmd, _ct) =
                            Task.FromResult<Result<RunningProcess, ProcessError>>(
                                Error(ProcessError.Unsupported "unused")
                            ) }

                match RecordReplayRunner.Auto(path, counting, options) with
                | Error error -> Assert.Fail $"auto load: {error}"
                | Ok auto ->
                    use auto = auto

                    let! _ = (runner auto).OutputStringAsync(command, CancellationToken.None)
                    // The freshly recorded (and projected) entry joins the live index under the same key
                    // the live call computes, so the repeat replays instead of reaching the inner runner.
                    let! _ = (runner auto).OutputStringAsync(command, CancellationToken.None)

                    Assert.That(calls, Is.EqualTo 1, "a projected Auto entry must replay within the session")

                    match auto.Save() with
                    | Ok() -> ()
                    | Error error -> Assert.Fail $"save: {error}"

                Assert.That(File.ReadAllText path, Does.Not.Contain "hunter2", "an Auto miss is projected too")

                // Reloaded from disk, the grown cassette replays the same call without delegating.
                match RecordReplayRunner.Auto(path, counting, options) with
                | Error error -> Assert.Fail $"auto reload: {error}"
                | Ok reloaded ->
                    use reloaded = reloaded

                    match! (runner reloaded).OutputStringAsync(command, CancellationToken.None) with
                    | Ok result -> Assert.That(result.Stdout, Is.EqualTo "live")
                    | Error error -> Assert.Fail $"a reloaded projected entry must replay: {error}"

                    Assert.That(calls, Is.EqualTo 1, "the reloaded entry must not reach the inner runner")
            })

    [<Test>]
    member _.``a projection that blanks the program stores a placeholder and still loads and replays``() : Task =
        withCassette (fun path ->
            task {
                // The format requires an entry to name a program (a blank one is rejected at load), so a
                // projection that returns none gets a fixed marker rather than an unloadable cassette.
                // Matching is unaffected either way — it never reads the stored name.
                let options =
                    RecordReplayOptions().WithCommandProjection(fun _ _ -> struct ("", [||]))

                let command =
                    Command.create "/tmp/hunter2-build/vault"
                    |> Command.args [ "--password=hunter2" ]

                do!
                    task {
                        use recorder = RecordReplayRunner.Record(path, FixedRunner("ok", 0), options)
                        let! _ = (runner recorder).OutputStringAsync(command, CancellationToken.None)
                        recorder.Complete()
                    }

                let onDisk = File.ReadAllText path
                Assert.That(onDisk, Does.Not.Contain "hunter2", "not even the program path may reach disk")
                Assert.That(onDisk, Does.Contain "(redacted)", "a blank projected program is stored as a marker")

                match RecordReplayRunner.Replay(path, options) with
                | Error error -> Assert.Fail $"a fully projected cassette must load: {error}"
                | Ok replayer ->
                    match! (runner replayer).OutputStringAsync(command, CancellationToken.None) with
                    | Ok result -> Assert.That(result.Stdout, Is.EqualTo "ok")
                    | Error error -> Assert.Fail $"a fully projected entry must still replay: {error}"
            })

    [<Test>]
    member _.``turning a projection on replays an existing cassette and projects only the rows it records``() : Task =
        withCassette (fun path ->
            task {
                // The realistic upgrade path: a cassette recorded before the hook existed, opened by an
                // Auto session that now has one. Its rows still replay (they key on their own verbatim
                // args, which are the invoked ones), and only what THIS session records is projected —
                // a projection is not a retroactive scrub of a file already on disk, so a fixture that
                // already carries a secret has to be re-recorded, not merely reopened.
                File.WriteAllText(
                    path,
                    """{ "Version": 8, "Entries": [ { "Program": "vault", "Args": ["--password=hunter2"], "HasStdin": false, "EnvNames": [], "EnvFingerprint": "1|default", "OutputWiring": "1|o:pipe|e:pipe", "Stdout": "old", "Stderr": "", "Code": 0 } ] }"""
                )

                let options =
                    RecordReplayOptions()
                        .WithCommandProjection(fun program args ->
                            struct (program, args |> Array.map (fun a -> a.Replace("hunter2", "[REDACTED]"))))

                let mutable calls = 0

                let counting =
                    { new IProcessRunner with
                        member _.CaptureStringAsync(cmd, _ct) =
                            calls <- calls + 1

                            Task.FromResult(
                                Ok(
                                    ProcessResult<string>(
                                        cmd.Program,
                                        "fresh",
                                        "",
                                        Outcome.Exited 0,
                                        TimeSpan.Zero,
                                        false,
                                        [ 0 ]
                                    )
                                )
                            )

                        member _.CaptureBytesAsync(_cmd, _ct) =
                            Task.FromResult<Result<ProcessResult<byte[]>, ProcessError>>(
                                Error(ProcessError.Unsupported "unused")
                            )

                        member _.SpawnAsync(_cmd, _ct) =
                            Task.FromResult<Result<RunningProcess, ProcessError>>(
                                Error(ProcessError.Unsupported "unused")
                            ) }

                match RecordReplayRunner.Auto(path, counting, options) with
                | Error error -> Assert.Fail $"auto load: {error}"
                | Ok auto ->
                    use auto = auto

                    let existing = Command.create "vault" |> Command.args [ "--password=hunter2" ]

                    match! (runner auto).OutputStringAsync(existing, CancellationToken.None) with
                    | Ok result -> Assert.That(result.Stdout, Is.EqualTo "old")
                    | Error error -> Assert.Fail $"an existing row must replay under a new projection: {error}"

                    Assert.That(calls, Is.EqualTo 0, "the existing row must not reach the inner runner")

                    let fresh = Command.create "vault" |> Command.args [ "--password=hunter2"; "renew" ]

                    let! _ = (runner auto).OutputStringAsync(fresh, CancellationToken.None)
                    Assert.That(calls, Is.EqualTo 1, "the new call is a miss and is recorded")

                    match auto.Save() with
                    | Ok() -> ()
                    | Error error -> Assert.Fail $"save: {error}"

                let onDisk = File.ReadAllText path

                Assert.That(
                    onDisk,
                    Does.Contain "\"--password=hunter2\"",
                    "a row recorded before the projection is preserved as it was, not retroactively scrubbed"
                )

                Assert.That(onDisk, Does.Contain "[REDACTED]", "the freshly recorded row is projected")
            })

    [<Test>]
    member _.``a pre-v9 entry keeps keying on its own verbatim program and args``() : Task =
        withCassette (fun path ->
            task {
                // Back-compat for the arguments specifically: an entry recorded before the fingerprint
                // existed carries none, so it is keyed from the program/args it stores — the invoked ones.
                File.WriteAllText(
                    path,
                    """{ "Version": 8, "Entries": [ { "Program": "tool", "Args": ["build", "--flag"], "HasStdin": false, "EnvNames": [], "EnvFingerprint": "1|default", "OutputWiring": "1|o:pipe|e:pipe", "Stdout": "old", "Stderr": "", "Code": 0 } ] }"""
                )

                match RecordReplayRunner.Replay path with
                | Error error -> Assert.Fail $"a v8 cassette must load: {error}"
                | Ok replayer ->
                    let matching = Command.create "tool" |> Command.args [ "build"; "--flag" ]

                    match! (runner replayer).OutputStringAsync(matching, CancellationToken.None) with
                    | Ok result -> Assert.That(result.Stdout, Is.EqualTo "old")
                    | Error error -> Assert.Fail $"a legacy entry must replay for its own args: {error}"

                    let different = Command.create "tool" |> Command.args [ "build"; "--other" ]

                    match! (runner replayer).OutputStringAsync(different, CancellationToken.None) with
                    | Error(ProcessError.CassetteMiss _) -> ()
                    | other -> Assert.Fail $"different args must still miss, got {other}"
            })

    [<Test>]
    member _.``Auto records a miss, replays a hit, and grows the cassette``() : Task =
        withCassette (fun path ->
            task {
                let mutable calls = 0

                let counting =
                    { new IProcessRunner with
                        member _.CaptureStringAsync(cmd, _ct) =
                            calls <- calls + 1

                            Task.FromResult(
                                Ok(
                                    ProcessResult<string>(
                                        cmd.Program,
                                        "live",
                                        "",
                                        Outcome.Exited 0,
                                        TimeSpan.Zero,
                                        false,
                                        [ 0 ]
                                    )
                                )
                            )

                        member _.CaptureBytesAsync(_cmd, _ct) =
                            Task.FromResult(Error(ProcessError.Unsupported "n/a"))

                        member _.SpawnAsync(_cmd, _ct) =
                            Task.FromResult(Error(ProcessError.Unsupported "n/a")) }

                let command = Command.create "tool" |> Command.arg "x"

                match RecordReplayRunner.Auto(path, counting) with
                | Error error -> Assert.Fail $"auto load: {error}"
                | Ok auto ->
                    do!
                        task {
                            use auto = auto
                            // First call: a miss delegates to the inner runner and records it.
                            let! first = (runner auto).OutputStringAsync(command, CancellationToken.None)
                            Assert.That(Result.isOk first, Is.True)
                            Assert.That(calls, Is.EqualTo 1)

                            // Second identical call: replays the just-recorded entry — the inner is not hit again.
                            let! _ = (runner auto).OutputStringAsync(command, CancellationToken.None)
                            Assert.That(calls, Is.EqualTo 1, "a recorded key must replay, not re-run the inner")

                            match auto.Save() with
                            | Ok() -> ()
                            | Error error -> Assert.Fail $"save: {error}"
                        }

                    // A fresh strict replay of the grown cassette hits the recorded entry.
                    match RecordReplayRunner.Replay path with
                    | Error error -> Assert.Fail $"{error}"
                    | Ok replayer ->
                        match! (runner replayer).OutputStringAsync(command, CancellationToken.None) with
                        | Ok result -> Assert.That(result.Stdout, Is.EqualTo "live")
                        | Error error -> Assert.Fail $"grown cassette must replay: {error}"
            })

    // --- Environment is part of the replay key (T-080) ---------------------------------------------

    [<Test>]
    member _.``a changed env value misses instead of replaying an unrelated recording``() : Task =
        withCassette (fun path ->
            task {
                // The security case: a test that swaps in a NEW secret must NOT get the OLD success back.
                let recorded = Command.create "tool" |> Command.env "TOKEN" "old-secret"
                let probe = Command.create "tool" |> Command.env "TOKEN" "new-secret"

                match! recordThenProbe path recorded probe with
                | Error(ProcessError.CassetteMiss _) -> Assert.Pass()
                | other -> Assert.Fail $"a changed env value must miss, got {other}"
            })

    [<Test>]
    member _.``the same effective env replays, and repeated overrides normalize``() : Task =
        withCassette (fun path ->
            task {
                // Recorded with a superseded earlier override; the probe expresses the same net effect (A=2)
                // directly. Repeated overrides with the same final effect must key identically and replay.
                let recorded = Command.create "tool" |> Command.env "A" "1" |> Command.env "A" "2"
                let probe = Command.create "tool" |> Command.env "A" "2"

                match! recordThenProbe path recorded probe with
                | Ok result -> Assert.That(result.Stdout, Is.EqualTo "recorded-output")
                | Error error -> Assert.Fail $"the same effective env must replay: {error}"
            })

    [<Test>]
    member _.``a different env name misses``() : Task =
        withCassette (fun path ->
            task {
                let recorded = Command.create "tool" |> Command.env "A" "x"
                let probe = Command.create "tool" |> Command.env "B" "x"

                match! recordThenProbe path recorded probe with
                | Error(ProcessError.CassetteMiss _) -> Assert.Pass()
                | other -> Assert.Fail $"a different env name must miss, got {other}"
            })

    [<Test>]
    member _.``an env removal is part of the key and normalizes``() : Task =
        withCassette (fun path ->
            task {
                // A removal is a distinct instruction: removing A is not the same as never touching it,
                // nor the same as setting it. A prior set that a removal cancels normalizes to the removal.
                let recorded = Command.create "tool" |> Command.env "A" "1" |> Command.envRemove "A"

                // A plain command (no override of A) must not falsely match the removal.
                match! recordThenProbe path recorded (Command.create "tool") with
                | Error(ProcessError.CassetteMiss _) -> ()
                | other ->
                    Assert.Fail $"a removal must not match a command that leaves the name untouched, got {other}"

                // The same net effect (just remove A) replays.
                match! recordThenProbe path recorded (Command.create "tool" |> Command.envRemove "A") with
                | Ok result -> Assert.That(result.Stdout, Is.EqualTo "recorded-output")
                | Error error -> Assert.Fail $"an equivalent removal must replay: {error}"

                // Removing A differs from setting A.
                match! recordThenProbe path recorded (Command.create "tool" |> Command.env "A" "1") with
                | Error(ProcessError.CassetteMiss _) -> Assert.Pass()
                | other -> Assert.Fail $"a removal must differ from a set of the same name, got {other}"
            })

    [<Test>]
    member _.``EnvClear is part of the key``() : Task =
        withCassette (fun path ->
            task {
                // A cleared environment is not the inherited one, even with no overrides — it must key apart.
                let recorded = Command.create "tool" |> Command.envClear

                match! recordThenProbe path recorded (Command.create "tool") with
                | Error(ProcessError.CassetteMiss _) -> ()
                | other -> Assert.Fail $"EnvClear must not match an un-cleared command, got {other}"

                // The same EnvClear replays.
                match! recordThenProbe path recorded (Command.create "tool" |> Command.envClear) with
                | Ok result -> Assert.That(result.Stdout, Is.EqualTo "recorded-output")
                | Error error -> Assert.Fail $"the same EnvClear must replay: {error}"
            })

    [<Test>]
    member _.``env name casing follows the platform's case sensitivity``() : Task =
        withCassette (fun path ->
            task {
                // Windows env names are case-insensitive (Path == PATH → same variable → replay); POSIX
                // names are case-sensitive (Path and PATH are different variables → miss).
                let recorded = Command.create "tool" |> Command.env "Path" "x"
                let probe = Command.create "tool" |> Command.env "PATH" "x"

                match! recordThenProbe path recorded probe with
                | result ->
                    if isWindows then
                        match result with
                        | Ok r -> Assert.That(r.Stdout, Is.EqualTo "recorded-output")
                        | Error error ->
                            Assert.Fail
                                $"on Windows a case-only difference is the same variable and must replay: {error}"
                    else
                        match result with
                        | Error(ProcessError.CassetteMiss _) -> Assert.Pass()
                        | other ->
                            Assert.Fail
                                $"on POSIX a case-only difference is a different variable and must miss, got {other}"
            })

    [<Test>]
    member _.``env values are never stored in the cassette``() : Task =
        withCassette (fun path ->
            task {
                do!
                    task {
                        use recorder = RecordReplayRunner.Record(path, FixedRunner("ok", 0))

                        let command =
                            Command.create "tool"
                            |> Command.env "TOKEN" "sup3r-s3cret-value"
                            |> Command.envRemove "REMOVED"

                        let! _ = (runner recorder).OutputStringAsync(command, CancellationToken.None)

                        match recorder.Save() with
                        | Ok() -> ()
                        | Error error -> Assert.Fail $"save: {error}"
                    }

                let onDisk = File.ReadAllText path

                Assert.That(
                    onDisk,
                    Does.Not.Contain "sup3r-s3cret-value",
                    "an env value must never reach disk in clear text"
                )
                // Names are not secret and stay inspectable; a redacting fingerprint replaces the values.
                Assert.That(onDisk, Does.Contain "TOKEN")
                Assert.That(onDisk, Does.Contain "EnvFingerprint")
            })

    [<Test>]
    member _.``a pre-v3 entry keys as the default environment and does not falsely match a customized call``() : Task =
        withCassette (fun path ->
            task {
                // A hand-written pre-v3 cassette (no EnvFingerprint field).
                File.WriteAllText(
                    path,
                    """{ "Version": 2, "Entries": [ { "Program": "tool", "Args": [], "HasStdin": false, "EnvNames": [], "Stdout": "old", "Stderr": "" } ] }"""
                )

                match RecordReplayRunner.Replay path with
                | Error error -> Assert.Fail $"a pre-v3 cassette must still load: {error}"
                | Ok replayer ->
                    // A default-env command replays it unchanged (backward compatible).
                    match! (runner replayer).OutputStringAsync(Command.create "tool", CancellationToken.None) with
                    | Ok result -> Assert.That(result.Stdout, Is.EqualTo "old")
                    | Error error -> Assert.Fail $"a default-env command must replay a pre-v3 entry: {error}"

                    // An env-customized command must NOT be handed the un-fingerprinted recording.
                    let customized = Command.create "tool" |> Command.env "TOKEN" "x"

                    match! (runner replayer).OutputStringAsync(customized, CancellationToken.None) with
                    | Error(ProcessError.CassetteMiss _) -> Assert.Pass()
                    | other -> Assert.Fail $"an env-customized call must not falsely match a pre-v3 entry, got {other}"
            })

    // --- Terminal-state / base64 validation (T-081) -------------------------------------------------

    [<Test>]
    member _.``corrupt base64 stdout gives the same Io error for string, bytes, and spawn replay``() : Task =
        withCassette (fun path ->
            task {
                // A hand-corrupted base64 payload: none of the three replay paths may silently swap it
                // for an empty/placeholder stdout — all three must report the SAME `ProcessError.Io`.
                File.WriteAllText(
                    path,
                    """{ "Version": 2, "Entries": [ { "Program": "tool", "Args": [], "HasStdin": false, "EnvNames": [], "Stdout": "", "Stderr": "", "StdoutBase64": "not-valid-base64!!", "Code": 0 } ] }"""
                )

                match RecordReplayRunner.Replay path with
                | Error error -> Assert.Fail $"replay load: {error}"
                | Ok replayer ->
                    let command = Command.create "tool"
                    let live = runner replayer

                    let! stringResult = live.CaptureStringAsync(command, CancellationToken.None)
                    let! bytesResult = live.CaptureBytesAsync(command, CancellationToken.None)
                    let! spawnResult = live.SpawnAsync(command, CancellationToken.None)

                    match stringResult, bytesResult, spawnResult with
                    | Error(ProcessError.Io stringMessage),
                      Error(ProcessError.Io bytesMessage),
                      Error(ProcessError.Io spawnMessage) ->
                        Assert.That(stringMessage, Is.EqualTo bytesMessage, "string vs bytes error message must match")
                        Assert.That(stringMessage, Is.EqualTo spawnMessage, "string vs spawn error message must match")
                    | other -> Assert.Fail $"expected Io from all three verbs for corrupt base64, got {other}"
            })

    [<Test>]
    member _.``an entry with no recorded terminal state replays as Unobserved, never a fabricated Exited 0``() : Task =
        withCassette (fun path ->
            task {
                // No TimedOut / Signal / Code at all (an omitted / hand-crafted entry) must never surface
                // as a fabricated clean exit.
                File.WriteAllText(
                    path,
                    """{ "Version": 1, "Entries": [ { "Program": "tool", "Args": [], "HasStdin": false, "EnvNames": [], "Stdout": "out", "Stderr": "" } ] }"""
                )

                match RecordReplayRunner.Replay path with
                | Error error -> Assert.Fail $"replay load: {error}"
                | Ok replayer ->
                    match! (runner replayer).CaptureStringAsync(Command.create "tool", CancellationToken.None) with
                    | Ok result ->
                        match result.Outcome with
                        | Outcome.Unobserved _ -> Assert.Pass()
                        | other -> Assert.Fail $"expected Outcome.Unobserved for a missing terminal state, got {other}"
                    | Error error -> Assert.Fail $"a missing terminal state must still replay: {error}"
            })

    [<Test>]
    member _.``a v5 entry without the v6 signal marker keeps a missing terminal state Unobserved``() : Task =
        withCassette (fun path ->
            task {
                // A v5 cassette predates `Signalled`; when every terminal-state member is absent it must
                // retain the old, honest partial-cassette behavior rather than infer a signal.
                File.WriteAllText(
                    path,
                    """{ "Version": 5, "Entries": [ { "Program": "legacy-partial", "Args": [], "HasStdin": false, "EnvNames": [], "Stdout": "out", "Stderr": "" } ] }"""
                )

                match RecordReplayRunner.Replay path with
                | Error error -> Assert.Fail $"a v5 cassette must load: {error}"
                | Ok replayer ->
                    match!
                        (runner replayer).CaptureStringAsync(Command.create "legacy-partial", CancellationToken.None)
                    with
                    | Ok result ->
                        match result.Outcome with
                        | Outcome.Unobserved _ -> ()
                        | other ->
                            Assert.Fail $"expected Outcome.Unobserved for a v5 missing terminal state, got {other}"
                    | Error error -> Assert.Fail $"a v5 missing terminal state must still replay: {error}"
            })

    [<Test>]
    member _.``recording and replaying Reply.Signalled preserves an unknown signal number``() : Task =
        withCassette (fun path ->
            task {
                let scripted: IProcessRunner = ScriptedRunner().Fallback(Reply.Signalled())

                do!
                    task {
                        use recorder = RecordReplayRunner.Record(path, scripted)

                        match!
                            (runner recorder).CaptureStringAsync(Command.create "signal-tool", CancellationToken.None)
                        with
                        | Ok result -> Assert.That(result.Outcome, Is.EqualTo(Outcome.Signalled None))
                        | Error error -> Assert.Fail $"recording Reply.Signalled failed: {error}"

                        match recorder.Save() with
                        | Ok() -> ()
                        | Error error -> Assert.Fail $"save: {error}"
                    }

                let onDisk = File.ReadAllText path
                Assert.That(onDisk, Does.Contain "\"Signalled\": true", "the v6 marker must be persisted")

                match RecordReplayRunner.Replay path with
                | Error error -> Assert.Fail $"replay load: {error}"
                | Ok replayer ->
                    match!
                        (runner replayer).CaptureStringAsync(Command.create "signal-tool", CancellationToken.None)
                    with
                    | Ok result -> Assert.That(result.Outcome, Is.EqualTo(Outcome.Signalled None))
                    | Error error -> Assert.Fail $"replaying Reply.Signalled failed: {error}"
            })

    [<Test>]
    member _.``a contradictory terminal state is rejected at load, naming the offending entry's index``() : Task =
        withCassette (fun path ->
            task {
                // Entry 0 is valid; entry 1 sets BOTH Signal and Code — mutually exclusive terminal states.
                File.WriteAllText(
                    path,
                    """{ "Version": 1, "Entries": [
                        { "Program": "ok", "Args": [], "HasStdin": false, "EnvNames": [], "Stdout": "", "Stderr": "", "Code": 0 },
                        { "Program": "tool", "Args": [], "HasStdin": false, "EnvNames": [], "Stdout": "", "Stderr": "", "Signal": 9, "Code": 0 }
                    ] }"""
                )

                match RecordReplayRunner.Replay path with
                | Error(ProcessError.Io message) ->
                    Assert.That(message, Does.Contain "entry 1", "the error must name the offending entry's index")
                | other -> Assert.Fail $"expected a rejected load for a contradictory terminal state, got {other}"
            })

    [<Test>]
    member _.``TimedOut combined with Code is also a contradictory terminal state``() : Task =
        withCassette (fun path ->
            task {
                File.WriteAllText(
                    path,
                    """{ "Version": 1, "Entries": [ { "Program": "tool", "Args": [], "HasStdin": false, "EnvNames": [], "Stdout": "", "Stderr": "", "TimedOut": true, "Code": 0 } ] }"""
                )

                match RecordReplayRunner.Replay path with
                | Error(ProcessError.Io message) -> Assert.That(message, Does.Contain "entry 0")
                | other -> Assert.Fail $"expected a rejected load for TimedOut+Code, got {other}"
            })

    [<Test>]
    member _.``a cassette entry missing the required Program field is rejected at load, naming its index``() : Task =
        withCassette (fun path ->
            task {
                File.WriteAllText(
                    path,
                    """{ "Version": 1, "Entries": [ { "Args": [], "HasStdin": false, "EnvNames": [], "Stdout": "", "Stderr": "" } ] }"""
                )

                match RecordReplayRunner.Replay path with
                | Error(ProcessError.Io message) ->
                    Assert.That(message, Does.Contain "entry 0", "the error must name the offending entry's index")
                | other -> Assert.Fail $"expected a rejected load for a missing Program, got {other}"
            })

    // --- PTY recordings and cassette schema migrations -----------------------------------------------

    [<Test>]
    member _.``recording a Command.Pty run writes a current-format cassette with the Pty flag and geometry``() : Task =
        withCassette (fun path ->
            task {
                do!
                    task {
                        use recorder = RecordReplayRunner.Record(path, FixedRunner("frame", 0))
                        let command = Command.create "tui" |> Command.pty
                        let! _ = (runner recorder).OutputStringAsync(command, CancellationToken.None)

                        match recorder.Save() with
                        | Ok() -> ()
                        | Error error -> Assert.Fail $"save: {error}"
                    }

                let onDisk = File.ReadAllText path
                Assert.That(onDisk, Does.Contain "\"Version\": 9", "a PTY recording writes the current format")
                Assert.That(onDisk, Does.Contain "\"Pty\": true")
                // PtyConfig.Default geometry is 80x24.
                Assert.That(onDisk, Does.Contain "\"PtyCols\": 80")
                Assert.That(onDisk, Does.Contain "\"PtyRows\": 24")
            })

    [<Test>]
    member _.``pre-v9 cassettes v1 through v8 still load and replay as recorded results under the v9 build``() : Task =
        task {
            // One hand-crafted fixture per legacy version. Each must load under the v9 build (a missing
            // Pty field defaults to false / non-PTY, a missing Signalled field preserves legacy signal
            // behavior, a missing Failure keeps the entry the recorded RESULT it always was, a missing
            // OutputWiring keys the entry as a legacy one served to a call that captures its output, and
            // a missing CommandFingerprint keys the entry from its own verbatim program/args) and replay
            // its recorded stdout, proving the whole v1→v9 back-compat load path, not just the newest
            // predecessor.
            let fixtures =
                [ 1,
                  "legacy1",
                  "one",
                  """{ "Version": 1, "Entries": [ { "Program": "legacy1", "Args": [], "HasStdin": false, "EnvNames": [], "Stdout": "one", "Stderr": "" } ] }"""
                  2,
                  "legacy2",
                  "two",
                  """{ "Version": 2, "Entries": [ { "Program": "legacy2", "Args": [], "HasStdin": false, "EnvNames": [], "Stdout": "two", "Stderr": "", "StdoutBase64": null } ] }"""
                  3,
                  "legacy3",
                  "three",
                  """{ "Version": 3, "Entries": [ { "Program": "legacy3", "Args": [], "HasStdin": false, "EnvNames": [], "EnvFingerprint": "1|default", "Stdout": "three", "Stderr": "" } ] }"""
                  4,
                  "legacy4",
                  "four",
                  """{ "Version": 4, "Entries": [ { "Program": "legacy4", "Args": [], "HasStdin": false, "EnvNames": [], "EnvFingerprint": "1|default", "Stdout": "four", "Stderr": "", "Pty": false } ] }"""
                  5,
                  "legacy5",
                  "five",
                  """{ "Version": 5, "Entries": [ { "Program": "legacy5", "Args": [], "HasStdin": false, "EnvNames": [], "EnvFingerprint": "1|default", "Stdout": "five", "Stderr": "", "Pty": false } ] }"""
                  6,
                  "legacy6",
                  "six",
                  """{ "Version": 6, "Entries": [ { "Program": "legacy6", "Args": [], "HasStdin": false, "EnvNames": [], "EnvFingerprint": "1|default", "Stdout": "six", "Stderr": "", "Pty": false, "Signalled": false, "Code": 0 } ] }"""
                  7,
                  "legacy7",
                  "seven",
                  """{ "Version": 7, "Entries": [ { "Program": "legacy7", "Args": [], "HasStdin": false, "EnvNames": [], "EnvFingerprint": "1|default", "Stdout": "seven", "Stderr": "", "Pty": false, "Signalled": false, "Code": 0, "Failure": null } ] }"""
                  8,
                  "legacy8",
                  "eight",
                  """{ "Version": 8, "Entries": [ { "Program": "legacy8", "Args": [], "HasStdin": false, "EnvNames": [], "EnvFingerprint": "1|default", "OutputWiring": "1|o:pipe|e:pipe", "Stdout": "eight", "Stderr": "", "Pty": false, "Signalled": false, "Code": 0, "Failure": null } ] }""" ]

            for version, program, expected, json in fixtures do
                let path = Path.GetTempFileName()

                try
                    File.WriteAllText(path, json)

                    match RecordReplayRunner.Replay path with
                    | Error error -> Assert.Fail $"a v{version} cassette must still load under the v9 build: {error}"
                    | Ok replayer ->
                        match! (runner replayer).OutputStringAsync(Command.create program, CancellationToken.None) with
                        | Ok result ->
                            Assert.That(result.Stdout, Is.EqualTo expected, $"v{version} entry must replay its stdout")
                            Assert.That(result.Stderr, Is.EqualTo "")
                        | Error error -> Assert.Fail $"a v{version} entry must replay: {error}"
                finally
                    if File.Exists path then
                        File.Delete path
        }

    [<Test>]
    member _.``a v4 cassette with a legacy stdin digest still replays``() : Task =
        withCassette (fun path ->
            task {
                let legacyDigest =
                    SHA256.HashData(Encoding.UTF8.GetBytes "inherit-stdin") |> Convert.ToHexString

                File.WriteAllText(
                    path,
                    $"""{{ "Version": 4, "Entries": [ {{ "Program": "legacy-stdin", "Args": [], "HasStdin": true, "StdinDigest": "{legacyDigest}", "EnvNames": [], "EnvFingerprint": "1|default", "Stdout": "old", "Stderr": "" }} ] }}"""
                )

                match RecordReplayRunner.Replay path with
                | Error error -> Assert.Fail $"a v4 cassette must load: {error}"
                | Ok replayer ->
                    let command = Command.create "legacy-stdin" |> Command.inheritStdin

                    match! (runner replayer).OutputStringAsync(command, CancellationToken.None) with
                    | Ok result -> Assert.That(result.Stdout, Is.EqualTo "old")
                    | Error error -> Assert.Fail $"a v4 legacy stdin digest must replay: {error}"
            })

    [<Test>]
    member _.``a redaction hook scrubs the merged PTY stream (an echoed credential) before it is stored``() : Task =
        withCassette (fun path ->
            task {
                // A PTY run captures ONE merged stream (D3); an interactively typed credential can be
                // echoed into it. The redaction hook must scrub that merged stdout before it reaches disk
                // — this proves the redactor covers the PTY stream, not just an ordinary stdout capture.
                let options =
                    RecordReplayOptions().WithRedaction(fun s -> s.Replace("hunter2", "[REDACTED]"))

                do!
                    task {
                        use recorder =
                            RecordReplayRunner.Record(path, FixedRunner("Password: hunter2\nlogged in", 0), options)

                        let command = Command.create "ssh" |> Command.pty
                        let! _ = (runner recorder).OutputStringAsync(command, CancellationToken.None)

                        match recorder.Save() with
                        | Ok() -> ()
                        | Error error -> Assert.Fail $"save: {error}"
                    }

                let onDisk = File.ReadAllText path

                Assert.That(
                    onDisk,
                    Does.Not.Contain "hunter2",
                    "the echoed credential must not reach the merged-stream recording"
                )

                Assert.That(onDisk, Does.Contain "[REDACTED]")
                Assert.That(onDisk, Does.Contain "\"Pty\": true", "the recording must be marked as a PTY run")

                // ...and the scrubbed merged stream is what replays.
                match RecordReplayRunner.Replay path with
                | Error error -> Assert.Fail $"{error}"
                | Ok replayer ->
                    match!
                        (runner replayer)
                            .OutputStringAsync(Command.create "ssh" |> Command.pty, CancellationToken.None)
                    with
                    | Ok result ->
                        Assert.That(result.Stdout, Does.Contain "[REDACTED]")
                        Assert.That(result.Stdout, Does.Not.Contain "hunter2")
                    | Error error -> Assert.Fail $"{error}"
            })

    [<Test>]
    member _.``a recorded PTY run replays through SpawnAsync as a merged stream (only Stdout events)``() : Task =
        withCassette (fun path ->
            task {
                do!
                    task {
                        use recorder = RecordReplayRunner.Record(path, FixedRunner("out1\nout2", 0))
                        let command = Command.create "tui" |> Command.pty
                        let! _ = (runner recorder).OutputStringAsync(command, CancellationToken.None)

                        match recorder.Save() with
                        | Ok() -> ()
                        | Error error -> Assert.Fail $"save: {error}"
                    }

                match RecordReplayRunner.Replay path with
                | Error error -> Assert.Fail $"{error}"
                | Ok replayer ->
                    match!
                        (runner replayer).SpawnAsync(Command.create "tui" |> Command.pty, CancellationToken.None)
                    with
                    | Error error -> Assert.Fail $"a PTY recording must replay a live handle: {error}"
                    | Ok proc ->
                        use proc = proc
                        let events = ResizeArray<OutputEvent>()
                        let enumerator = proc.OutputEventsAsync().GetAsyncEnumerator()
                        let mutable more = true

                        while more do
                            let! has = enumerator.MoveNextAsync()

                            if has then events.Add enumerator.Current else more <- false

                        do! enumerator.DisposeAsync()

                        Assert.That(
                            events
                            |> Seq.forall (fun e ->
                                match e with
                                | OutputEvent.Stdout _ -> true
                                | OutputEvent.Stderr _ -> false),
                            Is.True,
                            "a replayed PTY handle must emit only OutputEvent.Stdout"
                        )

                        CollectionAssert.AreEqual(
                            [| "out1"; "out2" |],
                            events |> Seq.map (fun e -> e.Text) |> Seq.toArray
                        )
            })

    // --- Recorded typed failures (cassette format v7) ------------------------------------------------
    //
    // A recording is only as honest as the calls it keeps: before v7 a call that ended in a typed
    // `ProcessError` was returned to the caller and written nowhere, so the very failures a test
    // records a cassette FOR — a missing program, a refused spawn, an unreadable stdin source, a
    // fail-loud output ceiling — replayed as `CassetteMiss`, and an Auto session re-ran the real tool
    // on every pass. These tests pin the recorded-failure half of the format: which errors are
    // recorded, that they come back as the same case and payload (never a generic `Io`/`CassetteMiss`),
    // that replay reaches no inner runner, and where the boundary against a racing cancellation sits.

    [<Test>]
    member _.``every recordable typed failure round-trips as the same case and payload, on both capture verbs``
        ()
        : Task =
        task {
            // One case per recordable `ProcessError`, plus the payload variants that must survive
            // DISTINCTLY: a `NotFound` whose search path is absent / empty / present (three different
            // facts), a signal with and without a number, each `OutputTooLarge` unit, and a JSON-RPC
            // `data` member both attached and absent.
            let failures =
                [ ProcessError.NotFound("not-found-searched", Some "/usr/bin:/bin")
                  ProcessError.NotFound("not-found-unsearched", None)
                  ProcessError.NotFound("not-found-empty-path", Some "")
                  ProcessError.Spawn("spawn-tool", "EACCES: permission denied")
                  ProcessError.Stdin("stdin-tool", "could not open /tmp/does-not-exist")
                  ProcessError.Exit("exit-tool", 3, "partial stdout", "boom")
                  ProcessError.Signalled("signalled-known", Some 9, "some stdout", "killed")
                  ProcessError.Signalled("signalled-unknown", None, "some stdout", "killed")
                  ProcessError.Timeout("timeout-tool", TimeSpan.FromMilliseconds 2500.0, "slow out", "slow err")
                  ProcessError.OutputTooLarge("too-many-lines", Some 10, None, 11, 4096)
                  ProcessError.OutputTooLarge("too-many-bytes", None, Some 1024, 0, 2048)
                  ProcessError.Parse("parse-tool", "unexpected token at offset 3")
                  ProcessError.JsonRpc(
                      "rpc-with-data",
                      "textDocument/hover",
                      -32601,
                      "method not found",
                      Some """{"retryable":false}"""
                  )
                  ProcessError.JsonRpc("rpc-without-data", "shutdown", -32602, "invalid params", None) ]

            for expected in failures do
                let program =
                    match expected.Program with
                    | Some name -> name
                    | None -> ""

                let path = Path.GetTempFileName()

                try
                    do!
                        task {
                            use recorder = RecordReplayRunner.Record(path, ErrorRunner [ expected ])
                            let recordLabel: string = $"{program}: record mode must return the live failure"

                            match!
                                (runner recorder).CaptureStringAsync(Command.create program, CancellationToken.None)
                            with
                            | Error error -> Assert.That(error, Is.EqualTo expected, recordLabel)
                            | Ok result ->
                                Assert.Fail
                                    $"the inner runner failed, so the recorder must not report success: {result.Stdout}"

                            match recorder.Save() with
                            | Ok() -> ()
                            | Error error -> Assert.Fail $"save ({program}): {error}"
                        }

                    // Replay mode holds NO inner runner at all, so whatever comes back here can only
                    // have come from the cassette — never from a subprocess or a delegate.
                    match RecordReplayRunner.Replay path with
                    | Error error -> Assert.Fail $"replay load ({program}): {error}"
                    | Ok replayer ->
                        use replayer = replayer
                        let stringLabel: string = $"{program} must replay as the same typed failure"

                        match! (runner replayer).CaptureStringAsync(Command.create program, CancellationToken.None) with
                        | Error error -> Assert.That(error, Is.EqualTo expected, stringLabel)
                        | Ok result -> Assert.Fail $"a recorded failure must not replay as a result: {result.Stdout}"

                        // A failure has no verb-specific payload, so the bytes verb replays the very
                        // same error rather than the "recorded as text, re-record for bytes" refusal a
                        // recorded RESULT would (correctly) give.
                        let bytesLabel: string = $"{program} must replay identically through the bytes verb"

                        match! (runner replayer).CaptureBytesAsync(Command.create program, CancellationToken.None) with
                        | Error error -> Assert.That(error, Is.EqualTo expected, bytesLabel)
                        | Ok result ->
                            Assert.Fail $"a recorded failure must not replay as bytes: {result.Stdout.Length} bytes"
                finally
                    deleteCassette path
        }

    [<Test>]
    member _.``recording a typed failure writes the v7 Failure form and no result half``() : Task =
        withCassette (fun path ->
            task {
                do!
                    task {
                        use recorder =
                            RecordReplayRunner.Record(
                                path,
                                ErrorRunner [ ProcessError.NotFound("git", Some "/usr/bin") ]
                            )

                        let! _ = (runner recorder).CaptureStringAsync(Command.create "git", CancellationToken.None)

                        match recorder.Save() with
                        | Ok() -> ()
                        | Error error -> Assert.Fail $"save: {error}"
                    }

                let onDisk = File.ReadAllText path
                Assert.That(onDisk, Does.Contain "\"Version\": 9", "a recorded failure writes the current format")
                Assert.That(onDisk, Does.Contain "\"Kind\": \"NotFound\"", "the discriminant names the error case")
                Assert.That(onDisk, Does.Contain "\"Searched\": \"/usr/bin\"", "the payload keeps the searched path")

                Assert.That(
                    onDisk,
                    Does.Not.Contain "\"Code\"",
                    "a failure entry records no result half — every unused field stays omitted"
                )
            })

    [<Test>]
    member _.``a recorded NotFound stores the child's effective PATH (an Env override included)``() : Task =
        withCassette (fun path ->
            task {
                // The single documented exception to this format's names-only environment rule, and a
                // security contract the docs (README, commands.md, hardening.md, testing.md) tell a
                // reader to rely on when deciding whether a fixture is committable: the searched path
                // of a recorded `NotFound` is an environment VALUE, and it is the child's EFFECTIVE
                // one — so a `PATH` the command set itself lands in the fixture verbatim. Pinned
                // through a real resolution (no child is ever spawned: the lookup fails first).
                let searchDir =
                    Path.Combine(Path.GetTempPath(), $"pk-empty-path-{Guid.NewGuid():N}")

                Directory.CreateDirectory searchDir |> ignore

                try
                    let command =
                        Command.create $"pk-missing-tool-{Guid.NewGuid():N}"
                        |> Command.env "PATH" searchDir

                    do!
                        task {
                            use recorder = RecordReplayRunner.Record(path, JobRunner())

                            match! (runner recorder).CaptureStringAsync(command, CancellationToken.None) with
                            | Error(ProcessError.NotFound(_, searched)) ->
                                Assert.That(
                                    searched,
                                    Is.EqualTo(Some searchDir),
                                    "the lookup walks the command's OVERRIDDEN PATH, not the process's own"
                                )
                            | other -> Assert.Fail $"expected NotFound for a missing program, got {other}"

                            match recorder.Save() with
                            | Ok() -> ()
                            | Error error -> Assert.Fail $"save: {error}"
                        }

                    use document = JsonDocument.Parse(readCassetteText path)

                    let recordedSearched =
                        document.RootElement.GetProperty("Entries").EnumerateArray()
                        |> Seq.map (fun entry ->
                            match entry.GetProperty("Failure").GetProperty("Searched").GetString() with
                            | null -> ""
                            | value -> value)
                        |> Seq.toArray

                    CollectionAssert.AreEqual(
                        [| searchDir |],
                        recordedSearched,
                        "the env value the lookup searched reaches disk verbatim — the documented exception"
                    )
                finally
                    Directory.Delete(searchDir, true)
            })

    [<Test>]
    member _.``an Auto session replays a recorded failure instead of calling the inner runner again``() : Task =
        withCassette (fun path ->
            task {
                let expected = ProcessError.NotFound("git", Some "/usr/bin")
                let inner = ErrorRunner [ expected ]

                let command () =
                    Command.create "git" |> Command.arg "status"

                match RecordReplayRunner.Auto(path, inner) with
                | Error error -> Assert.Fail $"auto load: {error}"
                | Ok recorder ->
                    use recorder = recorder

                    match! (runner recorder).CaptureStringAsync(command (), CancellationToken.None) with
                    | Error error -> Assert.That(error, Is.EqualTo expected, "the first call delegates and fails")
                    | Ok _ -> Assert.Fail "the inner runner failed, so the first call must fail too"

                    match! (runner recorder).CaptureStringAsync(command (), CancellationToken.None) with
                    | Error error -> Assert.That(error, Is.EqualTo expected, "the repeat replays the recorded failure")
                    | Ok _ -> Assert.Fail "the repeat must replay the recorded failure"

                    Assert.That(
                        inner.Calls,
                        Is.EqualTo 1,
                        "a recorded failure must replay without reaching the inner runner a second time"
                    )

                    match recorder.Save() with
                    | Ok() -> ()
                    | Error error -> Assert.Fail $"save: {error}"

                // ...and it was persisted, so a later hermetic session replays it too.
                CollectionAssert.AreEqual([| "git" |], cassettePrograms path)
            })

    [<Test>]
    member _.``duplicate recorded failures replay in capture order, then repeat the last``() : Task =
        withCassette (fun path ->
            task {
                let first = ProcessError.Exit("tool", 1, "", "first failure")
                let second = ProcessError.Exit("tool", 2, "", "second failure")

                do!
                    task {
                        use recorder = RecordReplayRunner.Record(path, ErrorRunner [ first; second ])

                        for _ in 1..2 do
                            let! _ =
                                (runner recorder).CaptureStringAsync(Command.create "tool", CancellationToken.None)

                            ()

                        match recorder.Save() with
                        | Ok() -> ()
                        | Error error -> Assert.Fail $"save: {error}"
                    }

                match RecordReplayRunner.Replay path with
                | Error error -> Assert.Fail $"replay load: {error}"
                | Ok replayer ->
                    use replayer = replayer

                    // The third call has no third recording: duplicates replay in capture order and then
                    // repeat the last one, exactly as duplicate RESULTS do.
                    for index, expected in List.indexed [ first; second; second ] do
                        let label: string = $"replay {index} must be the recorded failure for that position"

                        match! (runner replayer).CaptureStringAsync(Command.create "tool", CancellationToken.None) with
                        | Error error -> Assert.That(error, Is.EqualTo expected, label)
                        | Ok _ -> Assert.Fail $"replay {index} must be a failure"
            })

    [<Test>]
    member _.``the redaction hook scrubs a recorded failure's streams, detail, data, and searched path``() : Task =
        withCassette (fun path ->
            task {
                // The same hook that keeps a secret out of a recorded RESULT must keep it out of the
                // error half: an echoed credential can land in a failure's captured streams, in a spawn
                // detail built around a command line, in a JSON-RPC peer's `data`, or in the `PATH` a
                // not-found lookup reports.
                let options =
                    RecordReplayOptions().WithRedaction(fun s -> s.Replace("hunter2", "[REDACTED]"))

                let secrets =
                    [ ProcessError.Exit("exit-tool", 1, "token=hunter2", "auth failed for hunter2")
                      ProcessError.Spawn("spawn-tool", "could not run: --password=hunter2")
                      ProcessError.JsonRpc(
                          "rpc-tool",
                          "login",
                          -32000,
                          "denied for hunter2",
                          Some """{"pw":"hunter2"}"""
                      )
                      ProcessError.NotFound("which-tool", Some "/opt/hunter2/bin:/usr/bin") ]

                let scrubbed =
                    [ ProcessError.Exit("exit-tool", 1, "token=[REDACTED]", "auth failed for [REDACTED]")
                      ProcessError.Spawn("spawn-tool", "could not run: --password=[REDACTED]")
                      ProcessError.JsonRpc(
                          "rpc-tool",
                          "login",
                          -32000,
                          "denied for [REDACTED]",
                          Some """{"pw":"[REDACTED]"}"""
                      )
                      ProcessError.NotFound("which-tool", Some "/opt/[REDACTED]/bin:/usr/bin") ]

                do!
                    task {
                        use recorder = RecordReplayRunner.Record(path, ErrorRunner secrets, options)

                        for error in secrets do
                            let program =
                                match error.Program with
                                | Some name -> name
                                | None -> ""

                            let! _ =
                                (runner recorder).CaptureStringAsync(Command.create program, CancellationToken.None)

                            ()

                        match recorder.Save() with
                        | Ok() -> ()
                        | Error error -> Assert.Fail $"save: {error}"
                    }

                let onDisk = File.ReadAllText path

                Assert.That(
                    onDisk,
                    Does.Not.Contain "hunter2",
                    "no failure field may carry the secret to disk — streams, detail, data, or searched path"
                )

                Assert.That(onDisk, Does.Contain "[REDACTED]")

                // ...and the scrubbed payload is what replays.
                match RecordReplayRunner.Replay(path, options) with
                | Error error -> Assert.Fail $"replay load: {error}"
                | Ok replayer ->
                    use replayer = replayer

                    for expected in scrubbed do
                        let program =
                            match expected.Program with
                            | Some name -> name
                            | None -> ""

                        let label: string = $"{program} must replay its redacted payload"

                        match!
                            (runner replayer).CaptureStringAsync(Command.create program, CancellationToken.None)
                        with
                        | Error error -> Assert.That(error, Is.EqualTo expected, label)
                        | Ok _ -> Assert.Fail $"{program} must replay as a failure"
            })

    [<Test>]
    member _.``the record boundary is the token when the failure arrives, not the failure itself``() : Task =
        task {
            // The two halves of the boundary, differing ONLY in when the caller's token fires. Same
            // command, same inner failure, same recorder configuration:
            //   * live token when the failure arrives  -> the call completed, so it is recorded;
            //   * token already cancellation-requested -> the call was being torn down, so nothing is
            //     recorded (whether the cancellation caused the failure or merely raced it cannot be
            //     known here), while the caller still gets the inner failure verbatim rather than a
            //     relabelled `Cancelled`.
            let failure = ProcessError.Spawn("tool", "resource temporarily unavailable")

            let record (makeInner: unit -> IProcessRunner) (token: CancellationToken) : Task<string[]> =
                task {
                    let path = Path.GetTempFileName()

                    try
                        do!
                            task {
                                use recorder = RecordReplayRunner.Record(path, makeInner ())

                                match! (runner recorder).CaptureStringAsync(Command.create "tool", token) with
                                | Error error ->
                                    Assert.That(
                                        error,
                                        Is.EqualTo failure,
                                        "the caller sees the inner failure either way"
                                    )
                                | Ok _ -> Assert.Fail "the inner runner failed, so the call must fail"

                                match recorder.Save() with
                                | Ok() -> ()
                                | Error error -> Assert.Fail $"save: {error}"
                            }

                        return cassettePrograms path
                    finally
                        deleteCassette path
                }

            let! completed = record (fun () -> ErrorRunner [ failure ]) CancellationToken.None

            CollectionAssert.AreEqual(
                [| "tool" |],
                completed,
                "a failure that arrived with a live token is a completed call and is recorded"
            )

            use cancelling = new CancellationTokenSource()
            let! raced = record (fun () -> CancelThenFailRunner(cancelling, failure)) cancelling.Token

            Assert.That(
                raced,
                Is.Empty,
                "a failure that arrived with the token already cancelled must not reach the cassette"
            )
        }

    [<Test>]
    member _.``an error this format does not record is returned unrecorded and still misses on replay``() : Task =
        task {
            // Deliberately outside the recordable set: a cancellation (the caller's control flow), the
            // machinery's own miss, a nested retry-predicate failure this flat payload cannot hold, and
            // the transient/host-dependent kinds. Each must behave exactly as it did before failures
            // were recorded at all — returned to the caller, written nowhere.
            let unrecorded =
                [ ProcessError.Cancelled "tool"
                  ProcessError.CassetteMiss "tool"
                  ProcessError.Io "the disk went away"
                  ProcessError.Unsupported "not on this platform"
                  ProcessError.Unobserved("tool", "the exit status could not be read")
                  ProcessError.NotReady("tool", TimeSpan.FromSeconds 1.0)
                  ProcessError.ResourceLimit "no whole-tree limit primitive here"
                  ProcessError.Adopt(4321, "the target had already exited")
                  ProcessError.RetryPredicate("tool", ProcessError.Exit("tool", 1, "", ""), "the predicate threw") ]

            for expected in unrecorded do
                let path = Path.GetTempFileName()

                try
                    do!
                        task {
                            use recorder = RecordReplayRunner.Record(path, ErrorRunner [ expected ])
                            let label: string = $"the caller still sees {expected.Message}"

                            match!
                                (runner recorder).CaptureStringAsync(Command.create "tool", CancellationToken.None)
                            with
                            | Error error -> Assert.That(error, Is.EqualTo expected, label)
                            | Ok _ -> Assert.Fail "the inner runner failed, so the call must fail"

                            match recorder.Save() with
                            | Ok() -> ()
                            | Error error -> Assert.Fail $"save: {error}"
                        }

                    let emptyLabel: string = $"{expected.Message} must not be recorded"
                    Assert.That(cassettePrograms path, Is.Empty, emptyLabel)

                    match RecordReplayRunner.Replay path with
                    | Error error -> Assert.Fail $"replay load: {error}"
                    | Ok replayer ->
                        use replayer = replayer

                        match! (runner replayer).CaptureStringAsync(Command.create "tool", CancellationToken.None) with
                        | Error(ProcessError.CassetteMiss "tool") -> ()
                        | other -> Assert.Fail $"an unrecorded call must still miss on replay, got {other}"
                finally
                    deleteCassette path
        }

    [<Test>]
    member _.``a recorded launch failure replays through SpawnAsync as that failure, not a fake handle``() : Task =
        withCassette (fun path ->
            task {
                // `NotFound`/`Spawn` are launch failures: a real `SpawnAsync` of the same command
                // reported exactly this, so the replayed handle-producing verb must report it too rather
                // than starting a fake process from an entry that never held a result.
                let expected = ProcessError.NotFound("git", Some "/usr/bin:/bin")

                do!
                    task {
                        use recorder = RecordReplayRunner.Record(path, ErrorRunner [ expected ])
                        let! _ = (runner recorder).CaptureStringAsync(Command.create "git", CancellationToken.None)

                        match recorder.Save() with
                        | Ok() -> ()
                        | Error error -> Assert.Fail $"save: {error}"
                    }

                match RecordReplayRunner.Replay path with
                | Error error -> Assert.Fail $"replay load: {error}"
                | Ok replayer ->
                    use replayer = replayer

                    match! (runner replayer).SpawnAsync(Command.create "git", CancellationToken.None) with
                    | Error error -> Assert.That(error, Is.EqualTo expected, "the recorded failure replays as itself")
                    | Ok proc ->
                        use proc = proc
                        Assert.Fail $"a recorded failure must not replay as a live handle (pid {proc.Pid})"
            })

    [<Test>]
    member _.``a cassette mixing recorded results and recorded failures loads and replays both``() : Task =
        withCassette (fun path ->
            task {
                // The realistic shape of a grown cassette: some calls succeeded, some failed. Both halves
                // must load together and replay as what they are.
                let failure = ProcessError.Stdin("feeder", "could not open /tmp/gone")

                do!
                    task {
                        use recorder = RecordReplayRunner.Record(path, FixedRunner("recorded-output", 0))
                        let! _ = (runner recorder).CaptureStringAsync(Command.create "ok-tool", CancellationToken.None)

                        match recorder.Save() with
                        | Ok() -> ()
                        | Error error -> Assert.Fail $"save: {error}"
                    }

                match RecordReplayRunner.Auto(path, ErrorRunner [ failure ]) with
                | Error error -> Assert.Fail $"auto load: {error}"
                | Ok grower ->
                    use grower = grower
                    let! _ = (runner grower).CaptureStringAsync(Command.create "feeder", CancellationToken.None)

                    match grower.Save() with
                    | Ok() -> ()
                    | Error error -> Assert.Fail $"save: {error}"

                match RecordReplayRunner.Replay path with
                | Error error -> Assert.Fail $"a mixed cassette must load: {error}"
                | Ok replayer ->
                    use replayer = replayer

                    match! (runner replayer).CaptureStringAsync(Command.create "ok-tool", CancellationToken.None) with
                    | Ok result -> Assert.That(result.Stdout, Is.EqualTo "recorded-output")
                    | Error error -> Assert.Fail $"the recorded result must still replay: {error}"

                    match! (runner replayer).CaptureStringAsync(Command.create "feeder", CancellationToken.None) with
                    | Error error ->
                        Assert.That(error, Is.EqualTo failure, "the recorded failure must replay as itself")
                    | Ok _ -> Assert.Fail "the recorded failure must not replay as a result"
            })

    [<Test>]
    member _.``a crafted or incomplete recorded failure is rejected at load, naming the offending entry``() : Task =
        task {
            // A cassette is untrusted input: a failure payload this build cannot rebuild EXACTLY is
            // refused when the file loads (naming the row), never replayed as a different or generic
            // error, and never silently dropped so the entry replays as an empty success.
            let cases =
                [ "an unrecognized kind",
                  """{ "Version": 7, "Entries": [ { "Program": "tool", "Args": [], "HasStdin": false, "EnvNames": [], "Stdout": "", "Stderr": "", "Failure": { "Kind": "SomethingElse" } } ] }"""
                  "a failure with no kind at all",
                  """{ "Version": 7, "Entries": [ { "Program": "tool", "Args": [], "HasStdin": false, "EnvNames": [], "Stdout": "", "Stderr": "", "Failure": { "Detail": "boom" } } ] }"""
                  "an Exit failure without its code",
                  """{ "Version": 7, "Entries": [ { "Program": "tool", "Args": [], "HasStdin": false, "EnvNames": [], "Stdout": "", "Stderr": "", "Failure": { "Kind": "Exit", "Stderr": "boom" } } ] }"""
                  "a Timeout failure without its timeout",
                  """{ "Version": 7, "Entries": [ { "Program": "tool", "Args": [], "HasStdin": false, "EnvNames": [], "Stdout": "", "Stderr": "", "Failure": { "Kind": "Timeout" } } ] }"""
                  "a JsonRpc failure without its code",
                  """{ "Version": 7, "Entries": [ { "Program": "tool", "Args": [], "HasStdin": false, "EnvNames": [], "Stdout": "", "Stderr": "", "Failure": { "Kind": "JsonRpc", "Method": "m" } } ] }"""
                  "a failure alongside a recorded terminal state",
                  """{ "Version": 7, "Entries": [ { "Program": "tool", "Args": [], "HasStdin": false, "EnvNames": [], "Stdout": "", "Stderr": "", "Code": 0, "Failure": { "Kind": "Spawn", "Detail": "boom" } } ] }""" ]

            for label, json in cases do
                let path = Path.GetTempFileName()

                try
                    File.WriteAllText(path, json)

                    let indexLabel: string =
                        $"{label} must be rejected with the offending entry's index"

                    match RecordReplayRunner.Replay path with
                    | Error(ProcessError.Io message) -> Assert.That(message, Does.Contain "entry 0", indexLabel)
                    | other -> Assert.Fail $"expected {label} to be rejected at load, got {other}"
                finally
                    deleteCassette path
        }

    [<Test>]
    member _.``a crafted out-of-range failure timeout is clamped, not an overflow on replay``() : Task =
        withCassette (fun path ->
            task {
                // The same guarantee `DurationMs` already has: a hand-edited millisecond count far
                // beyond `TimeSpan`'s range must not turn a `Result`-returning verb into a throw.
                File.WriteAllText(
                    path,
                    """{ "Version": 7, "Entries": [ { "Program": "tool", "Args": [], "HasStdin": false, "EnvNames": [], "Stdout": "", "Stderr": "", "Failure": { "Kind": "Timeout", "TimeoutMs": 1e18, "Stderr": "slow" } } ] }"""
                )

                match RecordReplayRunner.Replay path with
                | Error error -> Assert.Fail $"a clampable timeout must still load: {error}"
                | Ok replayer ->
                    use replayer = replayer

                    match! (runner replayer).CaptureStringAsync(Command.create "tool", CancellationToken.None) with
                    | Error(ProcessError.Timeout("tool", timeout, _, stderr)) ->
                        Assert.That(timeout, Is.LessThanOrEqualTo TimeSpan.MaxValue)
                        Assert.That(stderr, Is.EqualTo "slow")
                    | other -> Assert.Fail $"expected a clamped Timeout failure, got {other}"
            })

    // --- Concurrent saves to one cassette path ---------------------------------------------------
    //
    // A cassette write is a full-file replacement, so two writers that overlap can silently lose a
    // recording: the older snapshot lands last and the newer one is gone, with both callers told `Ok`.
    // `Save` therefore runs its whole snapshot → write → rename → fsync section under this recorder's
    // save gate AND an advisory lock on a sibling `<path>.lock` shared with every other recorder and
    // process. The tests below pin the four promises that follow: a recorder never conflicts with
    // itself, a writer that loses the lock is refused with a typed retryable error instead of
    // clobbering, the file always holds one writer's whole snapshot, and an ordinary save neither
    // removes the lock file nor touches a temp it did not create.

    [<Test>]
    member _.``concurrent Save calls on one recorder all succeed and leave a whole cassette``() : Task =
        withCassette (fun path ->
            task {
                use recorder = RecordReplayRunner.Record(path, FixedRunner("out", 0))
                do! recordEntries recorder "tool" 20

                // A recorder is serialized against itself: overlapping saves queue up rather than
                // conflict, so none of them may be refused.
                let! results = Task.WhenAll [| for _ in 1..8 -> Task.Run(fun () -> recorder.Save()) |]

                for result in results do
                    match result with
                    | Ok() -> ()
                    | Error error -> Assert.Fail $"a recorder must never conflict with its own save: {error}"

                // ...and the file holds one complete snapshot, not a mixture of several writes.
                CollectionAssert.AreEqual(Array.create 20 "tool", cassettePrograms path)
            })

    [<Test>]
    member _.``a save that loses the sibling lock is refused and leaves the last cassette untouched``() : Task =
        withCassette (fun path ->
            task {
                use first = RecordReplayRunner.Record(path, FixedRunner("first", 0))
                do! recordEntries first "winner" 2

                match first.Save() with
                | Ok() -> ()
                | Error error -> Assert.Fail $"the first save must succeed: {error}"

                let before = File.ReadAllBytes path

                // A second recorder stands in for another process saving the same path.
                use second = RecordReplayRunner.Record(path, FixedRunner("second", 0))
                do! recordEntries second "loser" 1

                // Hold the lock through the very primitive `Save` acquires, so this is the real
                // contention path and not a re-implementation of it.
                match RecordReplayRunner.HoldSaveLockForTests path with
                | Error error -> Assert.Fail $"the test could not take the save lock: {error}"
                | Ok holder ->
                    let refused =
                        use _holder = holder
                        second.Save()

                    match refused with
                    | Error(ProcessError.Io _ as error) ->
                        Assert.That(error.IsTransient, Is.True, "a losing save must be retryable")
                    | other -> Assert.Fail $"a losing save must be a typed I/O conflict, got {other}"

                    CollectionAssert.AreEqual(
                        before,
                        File.ReadAllBytes path,
                        "a refused save must leave the last saved cassette byte for byte"
                    )

                // The lock is released now — as it is when a crashed writer's process dies, since the OS
                // drops it — so the next save goes through and publishes its own whole snapshot.
                match second.Save() with
                | Ok() -> ()
                | Error error -> Assert.Fail $"a save after the lock is released must succeed: {error}"

                CollectionAssert.AreEqual([| "loser" |], cassettePrograms path)
            })

    [<Test>]
    member _.``concurrent saves from several recorders leave one writer's whole snapshot, never a mix``() : Task =
        withCassette (fun path ->
            task {
                // Three recorders on one path, each with a recognisable program name and a different
                // number of entries — the stand-in for three processes sharing a cassette.
                let plan = [| "alpha", 3; "beta", 5; "gamma", 7 |]

                let recorders =
                    [| for program, _ in plan -> RecordReplayRunner.Record(path, FixedRunner(program, 0)) |]

                try
                    for index, (program, count) in Array.indexed plan do
                        do! recordEntries recorders[index] program count

                    let! results =
                        Task.WhenAll
                            [| for recorder in recorders -> Task.Run(fun () -> [| for _ in 1..5 -> recorder.Save() |]) |]

                    // Every save either wrote its whole snapshot or was told, in a typed and retryable
                    // way, that someone else was writing — never a silent partial success.
                    for result in Array.concat results do
                        match result with
                        | Ok() -> ()
                        | Error(ProcessError.Io _ as error) ->
                            Assert.That(error.IsTransient, Is.True, "a losing save must be retryable")
                        | Error other -> Assert.Fail $"a concurrent save must not fail as {other}"
                finally
                    for recorder in recorders do
                        (recorder :> IDisposable).Dispose()

                // Whatever the interleaving, the file is exactly one recorder's complete recording.
                let programs = cassettePrograms path
                let winner = Array.head programs
                let expected = plan |> Array.find (fun (program, _) -> program = winner) |> snd

                CollectionAssert.AreEqual(
                    Array.create expected winner,
                    programs,
                    "the cassette must hold one writer's whole snapshot, not a mixture"
                )
            })

    [<Test>]
    member _.``a later Save is never undone by an older one of the same recorder``() : Task =
        withCassette (fun path ->
            task {
                use recorder = RecordReplayRunner.Record(path, FixedRunner("out", 0))
                use stop = new CancellationTokenSource()

                // An observer of the cassette: because each save snapshots only once it already holds the
                // write lock, and the recording only grows, the entry count on disk can never go
                // backwards. A slower older save overwriting a newer one is exactly what that would look
                // like. Reads that lose a race with the rename are skipped, not failed.
                let highest = ref 0
                let wentBackwards = ref false

                let observer =
                    Task.Run(fun () ->
                        while not stop.IsCancellationRequested do
                            try
                                let seen = (cassettePrograms path).Length

                                if seen < highest.Value then
                                    wentBackwards.Value <- true
                                else
                                    highest.Value <- seen
                            with _ ->
                                // A read that lands mid-replacement (or before the first save) proves
                                // nothing either way; only the counts actually observed are asserted on.
                                ()

                            Thread.Sleep 1)

                for round in 1..30 do
                    let command = Command.create "tool" |> Command.arg (string round)
                    let! _ = (runner recorder).OutputStringAsync(command, CancellationToken.None)
                    // Four saves race each other while the recording keeps growing underneath them.
                    let! _ = Task.WhenAll [| for _ in 1..4 -> Task.Run(fun () -> recorder.Save()) |]
                    ()

                stop.Cancel()
                do! observer

                Assert.That(wentBackwards.Value, Is.False, "a completed save must never be replaced by an older one")

                match recorder.Save() with
                | Ok() -> ()
                | Error error -> Assert.Fail $"the final save must succeed: {error}"

                Assert.That((cassettePrograms path).Length, Is.EqualTo 30)
            })

    [<Test>]
    member _.``an ordinary save keeps the lock file and never touches a foreign temp``() : Task =
        withCassette (fun path ->
            task {
                let lockPath = path + ".lock"
                let directory = cassetteDirectory path
                let fileName = cassetteFileName path
                let tempPattern = fileName + ".tmp-*"

                // A leftover temp from a writer that crashed mid-save (or a live writer's in-flight temp,
                // which looks identical from here): a save must neither open nor remove it.
                let strayTemp = Path.Combine(directory, fileName + ".tmp-crashed-writer")

                File.WriteAllText(strayTemp, "a crashed writer's leftover")

                try
                    use recorder = RecordReplayRunner.Record(path, FixedRunner("out", 0))
                    do! recordEntries recorder "tool" 1

                    for _ in 1..2 do
                        match recorder.Save() with
                        | Ok() -> ()
                        | Error error -> Assert.Fail $"save: {error}"

                    Assert.That(
                        File.Exists lockPath,
                        Is.True,
                        "the lock file is a rendezvous, so an ordinary save must not delete and recreate it"
                    )

                    Assert.That(File.ReadAllText strayTemp, Is.EqualTo "a crashed writer's leftover")

                    CollectionAssert.AreEqual(
                        [| strayTemp |],
                        Directory.GetFiles(directory, tempPattern),
                        "a save must clean up its own temp and leave every other one alone"
                    )
                finally
                    if File.Exists strayTemp then
                        File.Delete strayTemp
            })

    [<Test>]
    member _.``disposing a recorder while another writer holds the lock neither throws nor clobbers``() : Task =
        withCassette (fun path ->
            task {
                use first = RecordReplayRunner.Record(path, FixedRunner("first", 0))
                do! recordEntries first "winner" 1

                match first.Save() with
                | Ok() -> ()
                | Error error -> Assert.Fail $"the first save must succeed: {error}"

                let before = File.ReadAllBytes path

                match RecordReplayRunner.HoldSaveLockForTests path with
                | Error error -> Assert.Fail $"the test could not take the save lock: {error}"
                | Ok holder ->
                    use _holder = holder
                    let second = RecordReplayRunner.Record(path, FixedRunner("second", 0))
                    do! recordEntries second "loser" 1
                    // Declared finished, so the dispose below really does reach the flush path this test
                    // is about — an uncompleted recording would skip it and prove nothing about locking.
                    second.Complete()

                    // The drop-time flush is best-effort in both directions: a lock it cannot get within
                    // its short wait is given up on rather than thrown out of `Dispose`, and the cassette
                    // the lock holder is protecting stays exactly as it was.
                    Assert.DoesNotThrow(Action(fun () -> (second :> IDisposable).Dispose()))

                    CollectionAssert.AreEqual(
                        before,
                        File.ReadAllBytes path,
                        "a flush that lost the lock must not touch the saved cassette"
                    )
            })

    [<Test>]
    member _.``a save to an unusable path fails as itself, not as a concurrency conflict``() : Task =
        task {
            // A cassette under a directory that does not exist can never be written, however long the
            // caller waits — so it must report that, not the retryable "another writer has the lock"
            // conflict a save uses for real contention.
            let missing =
                Path.Combine(Path.GetTempPath(), $"pk-missing-{Guid.NewGuid():N}", "cassette.json")

            use recorder = RecordReplayRunner.Record(missing, FixedRunner("out", 0))
            do! recordEntries recorder "tool" 1

            match recorder.Save() with
            | Ok() -> Assert.Fail "a save into a missing directory must not report success"
            | Error(ProcessError.Io detail) ->
                Assert.That(
                    detail,
                    Does.Not.Contain "another writer",
                    "a broken path must not be reported as a retryable conflict"
                )
            | Error other -> Assert.Fail $"expected a typed I/O error, got {other}"
        }

    [<Test>]
    member _.``the parent-directory fsync a save performs reaches the platform``() : Task =
        withCassette (fun path ->
            task {
                // The rename that publishes a cassette is a directory-metadata write of its own, so a
                // save fsyncs the parent directory on Unix (a documented no-op on Windows). That call is
                // best-effort inside `Save`, which means a binding that never resolves would look exactly
                // like success — this asserts it actually runs.
                match RecordReplayRunner.FsyncParentDirectoryForTests path with
                | Ok() -> ()
                | Error detail -> Assert.Fail $"the parent-directory fsync must reach the platform: {detail}"
            })

    // --- Output-wiring fidelity (cassette format v8) -------------------------------------------------
    //
    // A real run captures stdout only where it actually reaches the parent — over a pipe, or a PTY's
    // merged terminal. `Stdout(Null)`, inherited stdout, and a direct `StdoutToFile` redirect all leave
    // the capture verbs honestly empty. Replay has to agree: a recording made over a pipe must not hand
    // its captured output to a call whose stdout goes nowhere near the parent, and a recording made with
    // stdout going elsewhere must not answer a piped call with its (empty) capture. The wiring is part
    // of the match key, so a wiring the cassette never recorded is an ordinary miss.

    [<Test>]
    member _.``a piped recording is not replayed for a Stdout(Null) call``() : Task =
        withCassette (fun path ->
            task {
                let recorded = Command.create "tool"
                let probe = Command.create "tool" |> Command.stdout StdioMode.Null

                match! recordThenProbe path recorded probe with
                | Error(ProcessError.CassetteMiss "tool") -> ()
                | other -> Assert.Fail $"a Null-stdout call must not replay a piped recording, got {other}"
            })

    [<Test>]
    member _.``a piped recording is not replayed for an inherited-stdout call``() : Task =
        withCassette (fun path ->
            task {
                let recorded = Command.create "tool"
                let probe = Command.create "tool" |> Command.stdout StdioMode.Inherit

                match! recordThenProbe path recorded probe with
                | Error(ProcessError.CassetteMiss "tool") -> ()
                | other -> Assert.Fail $"an inherited-stdout call must not replay a piped recording, got {other}"
            })

    [<Test>]
    member _.``a piped recording is not replayed for a StdoutToFile call, and no file is invented``() : Task =
        withCassette (fun path ->
            task {
                let redirect = redirectPath ()

                try
                    let recorded = Command.create "tool"
                    let probe = Command.create "tool" |> Command.stdoutToFile redirect false

                    match! recordThenProbe path recorded probe with
                    | Error(ProcessError.CassetteMiss "tool") -> ()
                    | other -> Assert.Fail $"a file-redirected call must not replay a piped recording, got {other}"

                    // Replay spawns nothing, so the redirect target must not exist either: a miss can
                    // never leave a caller believing the file was written.
                    Assert.That(File.Exists redirect, Is.False, "replay must not invent the redirect file")
                finally
                    if File.Exists redirect then
                        File.Delete redirect
            })

    [<Test>]
    member _.``a recording whose stdout went nowhere is not replayed for a piped call``() : Task =
        withCassette (fun path ->
            task {
                // The reverse pair: recorded through a runner that captures what a real run would, so
                // the entry honestly holds NO stdout. Replaying it for a piped call would hide that
                // call's real output behind an empty capture.
                let recorded = Command.create "tool" |> Command.stdout StdioMode.Null
                let probe = Command.create "tool"

                match! recordThenProbeVia (WiringAwareRunner("captured", "")) path recorded probe with
                | Error(ProcessError.CassetteMiss "tool") -> ()
                | other -> Assert.Fail $"a piped call must not replay a Null-stdout recording, got {other}"
            })

    [<Test>]
    member _.``the same stdout wiring still replays``() : Task =
        withCassette (fun path ->
            task {
                // No regression for the honest case: one wiring, recorded and replayed, keeps working —
                // and a non-capturing wiring replays as the empty capture it recorded.
                let silent = Command.create "tool" |> Command.stdout StdioMode.Null

                match! recordThenProbeVia (WiringAwareRunner("captured", "")) path silent silent with
                | Ok result ->
                    Assert.That(result.Stdout, Is.Empty, "a Null-stdout run captures nothing, on replay too")
                    Assert.That(result.Outcome, Is.EqualTo(Outcome.Exited 0))
                | Error error -> Assert.Fail $"the same wiring must still replay: {error}"
            })

    [<Test>]
    member _.``a piped recording still replays unchanged``() : Task =
        withCassette (fun path ->
            task {
                let command = Command.create "tool" |> Command.arg "x"

                match! recordThenProbe path command command with
                | Ok result -> Assert.That(result.Stdout, Is.EqualTo "recorded-output")
                | Error error -> Assert.Fail $"an ordinary piped recording must replay: {error}"
            })

    [<Test>]
    member _.``a file redirect keys its path and its append flag``() : Task =
        withCassette (fun path ->
            task {
                let first = redirectPath ()
                let second = redirectPath ()

                let toFile (target: string) (append: bool) =
                    Command.create "tool" |> Command.stdoutToFile target append

                // Same file, appended rather than truncated: a different destination, so a miss.
                match!
                    recordThenProbeVia
                        (WiringAwareRunner("captured", ""))
                        path
                        (toFile first false)
                        (toFile first true)
                with
                | Error(ProcessError.CassetteMiss "tool") -> ()
                | other -> Assert.Fail $"append and truncate are different wirings, got {other}"

                // A different file is a different destination too.
                match!
                    recordThenProbeVia
                        (WiringAwareRunner("captured", ""))
                        path
                        (toFile first false)
                        (toFile second false)
                with
                | Error(ProcessError.CassetteMiss "tool") -> ()
                | other -> Assert.Fail $"two redirect targets are different wirings, got {other}"

                // The identical redirect replays.
                match!
                    recordThenProbeVia
                        (WiringAwareRunner("captured", ""))
                        path
                        (toFile first false)
                        (toFile first false)
                with
                | Ok result -> Assert.That(result.Stdout, Is.Empty)
                | Error error -> Assert.Fail $"the identical redirect must replay: {error}"
            })

    [<Test>]
    member _.``a stderr destination and MergeStderr are part of the wiring key``() : Task =
        withCassette (fun path ->
            task {
                let recorded = Command.create "tool"

                // stderr folded into stdout: `ProcessResult.Stderr` is empty on a real run, so a
                // separate-streams recording must not answer it.
                match!
                    recordThenProbeVia
                        (WiringAwareRunner("out", "err"))
                        path
                        recorded
                        (Command.create "tool" |> Command.mergeStderr)
                with
                | Error(ProcessError.CassetteMiss "tool") -> ()
                | other ->
                    Assert.Fail $"a merged-stderr call must not replay a separate-streams recording, got {other}"

                match!
                    recordThenProbeVia
                        (WiringAwareRunner("out", "err"))
                        path
                        recorded
                        (Command.create "tool" |> Command.stderr StdioMode.Null)
                with
                | Error(ProcessError.CassetteMiss "tool") -> ()
                | other -> Assert.Fail $"a Null-stderr call must not replay a captured stderr, got {other}"
            })

    [<Test>]
    member _.``a PTY recording and a plain run are different wirings``() : Task =
        withCassette (fun path ->
            task {
                let terminal = Command.create "tui" |> Command.pty

                match! recordThenProbeVia (WiringAwareRunner("frame", "")) path terminal (Command.create "tui") with
                | Error(ProcessError.CassetteMiss "tui") -> ()
                | other -> Assert.Fail $"a plain run must not replay a PTY recording, got {other}"

                match! recordThenProbeVia (WiringAwareRunner("frame", "")) path terminal terminal with
                | Ok result -> Assert.That(result.Stdout, Is.EqualTo "frame")
                | Error error -> Assert.Fail $"a PTY recording must replay for a PTY call: {error}"
            })

    [<Test>]
    member _.``the bytes verb applies the same wiring boundary``() : Task =
        withCassette (fun path ->
            task {
                do!
                    task {
                        use recorder = RecordReplayRunner.Record(path, WiringAwareRunner("captured", ""))
                        let! _ = (runner recorder).CaptureBytesAsync(Command.create "tool", CancellationToken.None)

                        match recorder.Save() with
                        | Ok() -> ()
                        | Error error -> Assert.Fail $"save: {error}"
                    }

                match RecordReplayRunner.Replay path with
                | Error error -> Assert.Fail $"replay load: {error}"
                | Ok replayer ->
                    let silent = Command.create "tool" |> Command.stdout StdioMode.Null

                    match! (runner replayer).CaptureBytesAsync(silent, CancellationToken.None) with
                    | Error(ProcessError.CassetteMiss "tool") -> ()
                    | other ->
                        Assert.Fail $"the bytes verb must not replay a piped recording for a Null call, got {other}"

                    match! (runner replayer).CaptureBytesAsync(Command.create "tool", CancellationToken.None) with
                    | Ok result -> Assert.That(Encoding.UTF8.GetString result.Stdout, Is.EqualTo "captured")
                    | Error error -> Assert.Fail $"the identical wiring must still replay exact bytes: {error}"
            })

    [<Test>]
    member _.``a replayed SpawnAsync applies the same wiring boundary``() : Task =
        withCassette (fun path ->
            task {
                do!
                    task {
                        use recorder = RecordReplayRunner.Record(path, WiringAwareRunner("line one\n", ""))
                        let! _ = (runner recorder).OutputStringAsync(Command.create "tool", CancellationToken.None)

                        match recorder.Save() with
                        | Ok() -> ()
                        | Error error -> Assert.Fail $"save: {error}"
                    }

                match RecordReplayRunner.Replay path with
                | Error error -> Assert.Fail $"replay load: {error}"
                | Ok replayer ->
                    let silent = Command.create "tool" |> Command.stdout StdioMode.Null

                    match! (runner replayer).StartAsync(silent, CancellationToken.None) with
                    | Error(ProcessError.CassetteMiss "tool") -> ()
                    | Ok _ -> Assert.Fail "a Null-stdout spawn must not replay a piped recording"
                    | Error other -> Assert.Fail $"expected a cassette miss, got {other}"

                    match! (runner replayer).StartAsync(Command.create "tool", CancellationToken.None) with
                    | Error error -> Assert.Fail $"the identical wiring must still replay a handle: {error}"
                    | Ok running ->
                        use running = running

                        match! running.OutputStringAsync() with
                        // The reconstructed handle re-splits the recorded stream into lines, so the
                        // recording's trailing newline is a line terminator rather than content.
                        | Ok result -> Assert.That(result.Stdout, Is.EqualTo "line one")
                        | Error error -> Assert.Fail $"the replayed handle must stream the recording: {error}"
            })

    [<Test>]
    member _.``a recording stores its wiring fingerprint, keeping a redirect path out of the file``() : Task =
        withCassette (fun path ->
            task {
                let redirect = redirectPath ()

                try
                    do!
                        task {
                            use recorder = RecordReplayRunner.Record(path, WiringAwareRunner("captured", ""))
                            let command = Command.create "tool" |> Command.stdoutToFile redirect false
                            let! _ = (runner recorder).OutputStringAsync(command, CancellationToken.None)

                            match recorder.Save() with
                            | Ok() -> ()
                            | Error error -> Assert.Fail $"save: {error}"
                        }

                    let onDisk = File.ReadAllText path
                    Assert.That(onDisk, Does.Contain "\"Version\": 9", "a recording writes the current format")
                    Assert.That(onDisk, Does.Contain "\"OutputWiring\"", "every recording stores its own wiring")
                    Assert.That(onDisk, Does.Contain "o:file:", "a redirect keys as a file destination")

                    // The path itself is digested, exactly as env values are: it can carry as much (a
                    // token in a temp directory's name), so it must not land in the file in clear text —
                    // checked against both its JSON-escaped form and its bare file name.
                    let escapedPath = (JsonSerializer.Serialize redirect).Trim '"'

                    Assert.That(onDisk, Does.Not.Contain escapedPath, "a redirect path must not be stored verbatim")

                    Assert.That(
                        onDisk,
                        Does.Not.Contain(Path.GetFileName redirect),
                        "not even the redirect's file name may reach the cassette"
                    )
                finally
                    if File.Exists redirect then
                        File.Delete redirect
            })

    [<Test>]
    member _.``Auto records a separate entry for a different stdout wiring instead of replaying the first``() : Task =
        withCassette (fun path ->
            task {
                match RecordReplayRunner.Auto(path, WiringAwareRunner("captured", "")) with
                | Error error -> Assert.Fail $"auto load: {error}"
                | Ok auto ->
                    use auto = auto
                    let piped = Command.create "tool"
                    let silent = Command.create "tool" |> Command.stdout StdioMode.Null

                    match! (runner auto).OutputStringAsync(piped, CancellationToken.None) with
                    | Ok result -> Assert.That(result.Stdout, Is.EqualTo "captured")
                    | Error error -> Assert.Fail $"the piped miss must be delegated and recorded: {error}"

                    match! (runner auto).OutputStringAsync(silent, CancellationToken.None) with
                    | Ok result ->
                        Assert.That(result.Stdout, Is.Empty, "the Null-stdout call must run for real, not replay")
                    | Error error -> Assert.Fail $"the second wiring must be delegated and recorded: {error}"

                    match auto.Save() with
                    | Ok() -> ()
                    | Error error -> Assert.Fail $"save: {error}"

                    Assert.That(
                        (cassettePrograms path).Length,
                        Is.EqualTo 2,
                        "two wirings of one call are two recordings, not one shared entry"
                    )
            })

    // --- Pre-v8 (no recorded wiring) compatibility ---------------------------------------------------
    //
    // A cassette written before v8 says nothing about how its calls were wired, so it is served only
    // where handing it over cannot fabricate anything: the PTY shape must agree (recorded since v4), and
    // a call that captures nothing on a channel may only be given an entry that recorded nothing there.
    // Everything else about such an entry replays exactly as it did before v8.

    [<Test>]
    member _.``a pre-v8 entry holding captured stdout still replays for a piped call``() : Task =
        withCassette (fun path ->
            task {
                File.WriteAllText(
                    path,
                    """{ "Version": 7, "Entries": [ { "Program": "tool", "Args": [], "HasStdin": false, "EnvNames": [], "EnvFingerprint": "1|default", "Stdout": "old", "Stderr": "", "Code": 0 } ] }"""
                )

                match RecordReplayRunner.Replay path with
                | Error error -> Assert.Fail $"a v7 cassette must load: {error}"
                | Ok replayer ->
                    match! (runner replayer).OutputStringAsync(Command.create "tool", CancellationToken.None) with
                    | Ok result -> Assert.That(result.Stdout, Is.EqualTo "old", "an existing cassette keeps replaying")
                    | Error error -> Assert.Fail $"a legacy entry must still replay for a piped call: {error}"
            })

    [<Test>]
    member _.``a pre-v8 entry holding captured stdout is refused by a call that captures none``() : Task =
        task {
            let redirect = redirectPath ()

            let probes =
                [ "Null", Command.create "tool" |> Command.stdout StdioMode.Null
                  "Inherit", Command.create "tool" |> Command.stdout StdioMode.Inherit
                  "StdoutToFile", Command.create "tool" |> Command.stdoutToFile redirect false ]

            for label, probe in probes do
                let path = Path.GetTempFileName()

                try
                    File.WriteAllText(
                        path,
                        """{ "Version": 7, "Entries": [ { "Program": "tool", "Args": [], "HasStdin": false, "EnvNames": [], "EnvFingerprint": "1|default", "Stdout": "old", "Stderr": "", "Code": 0 } ] }"""
                    )

                    match RecordReplayRunner.Replay path with
                    | Error error -> Assert.Fail $"a v7 cassette must load: {error}"
                    | Ok replayer ->
                        match! (runner replayer).OutputStringAsync(probe, CancellationToken.None) with
                        | Error(ProcessError.CassetteMiss "tool") -> ()
                        | other -> Assert.Fail $"a legacy entry must not fabricate stdout for {label}, got {other}"
                finally
                    if File.Exists path then
                        File.Delete path

            Assert.That(File.Exists redirect, Is.False, "replay must not invent the redirect file")
        }

    [<Test>]
    member _.``a pre-v8 entry that captured nothing still replays for a call that captures nothing``() : Task =
        withCassette (fun path ->
            task {
                // Nothing can be fabricated from an entry with no captured output, so the legacy
                // fallback still serves it — and it replays as the honest empty capture it recorded.
                File.WriteAllText(
                    path,
                    """{ "Version": 7, "Entries": [ { "Program": "tool", "Args": [], "HasStdin": false, "EnvNames": [], "EnvFingerprint": "1|default", "Stdout": "", "Stderr": "", "Code": 0 } ] }"""
                )

                match RecordReplayRunner.Replay path with
                | Error error -> Assert.Fail $"a v7 cassette must load: {error}"
                | Ok replayer ->
                    let silent = Command.create "tool" |> Command.stdout StdioMode.Null

                    match! (runner replayer).OutputStringAsync(silent, CancellationToken.None) with
                    | Ok result ->
                        Assert.That(result.Stdout, Is.Empty)
                        Assert.That(result.Outcome, Is.EqualTo(Outcome.Exited 0))
                    | Error error -> Assert.Fail $"an empty legacy entry must still replay: {error}"
            })

    [<Test>]
    member _.``a pre-v8 entry that captured nothing still replays for a piped call``() : Task =
        withCassette (fun path ->
            task {
                // The other half of the empty-entry rule, stated on purpose: a pre-v8 file cannot say
                // whether this entry came from a silent piped command or from one whose output went
                // elsewhere, and an empty capture invents nothing either way — so it keeps replaying for
                // both, exactly as it did before v8. Only re-recording it (which writes a wiring) can
                // tell the two apart.
                File.WriteAllText(
                    path,
                    """{ "Version": 7, "Entries": [ { "Program": "tool", "Args": [], "HasStdin": false, "EnvNames": [], "EnvFingerprint": "1|default", "Stdout": "", "Stderr": "", "Code": 0 } ] }"""
                )

                match RecordReplayRunner.Replay path with
                | Error error -> Assert.Fail $"a v7 cassette must load: {error}"
                | Ok replayer ->
                    match! (runner replayer).OutputStringAsync(Command.create "tool", CancellationToken.None) with
                    | Ok result -> Assert.That(result.Stdout, Is.Empty)
                    | Error error -> Assert.Fail $"an empty legacy entry must still replay for a piped call: {error}"
            })

    [<Test>]
    member _.``a pre-v8 entry holding captured stderr is refused by a merged-stderr call``() : Task =
        withCassette (fun path ->
            task {
                File.WriteAllText(
                    path,
                    """{ "Version": 7, "Entries": [ { "Program": "tool", "Args": [], "HasStdin": false, "EnvNames": [], "EnvFingerprint": "1|default", "Stdout": "", "Stderr": "boom", "Code": 0 } ] }"""
                )

                match RecordReplayRunner.Replay path with
                | Error error -> Assert.Fail $"a v7 cassette must load: {error}"
                | Ok replayer ->
                    let merged = Command.create "tool" |> Command.mergeStderr

                    match! (runner replayer).OutputStringAsync(merged, CancellationToken.None) with
                    | Error(ProcessError.CassetteMiss "tool") -> ()
                    | other ->
                        Assert.Fail $"a merged run's stderr is empty — a legacy entry must not fill it, got {other}"
            })

    [<Test>]
    member _.``a pre-v8 PTY entry and a plain call do not match either way``() : Task =
        task {
            let fixtures =
                [ "merged",
                  """{ "Version": 7, "Entries": [ { "Program": "tui", "Args": [], "HasStdin": false, "EnvNames": [], "EnvFingerprint": "1|default", "Stdout": "frame", "Stderr": "", "Pty": true, "PtyCols": 80, "PtyRows": 24, "Code": 0 } ] }""",
                  Command.create "tui",
                  (Command.create "tui" |> Command.pty)
                  "plain",
                  """{ "Version": 7, "Entries": [ { "Program": "tui", "Args": [], "HasStdin": false, "EnvNames": [], "EnvFingerprint": "1|default", "Stdout": "frame", "Stderr": "", "Pty": false, "Code": 0 } ] }""",
                  (Command.create "tui" |> Command.pty),
                  Command.create "tui" ]

            for label, json, mismatched, matching in fixtures do
                let path = Path.GetTempFileName()

                try
                    File.WriteAllText(path, json)

                    match RecordReplayRunner.Replay path with
                    | Error error -> Assert.Fail $"a v7 {label} cassette must load: {error}"
                    | Ok replayer ->
                        match! (runner replayer).OutputStringAsync(mismatched, CancellationToken.None) with
                        | Error(ProcessError.CassetteMiss "tui") -> ()
                        | other -> Assert.Fail $"a legacy {label} recording must not serve the other shape, got {other}"

                        match! (runner replayer).OutputStringAsync(matching, CancellationToken.None) with
                        | Ok result -> Assert.That(result.Stdout, Is.EqualTo "frame")
                        | Error error ->
                            Assert.Fail $"a legacy {label} recording must replay for its own shape: {error}"
                finally
                    if File.Exists path then
                        File.Delete path
        }

    [<Test>]
    member _.``a refused pre-v8 candidate does not consume its group's capture order``() : Task =
        withCassette (fun path ->
            task {
                // Two pre-v8 recordings of one call, which replay in capture order. A call that captures
                // no stdout is refused both of them; that refusal must leave the group's cursor exactly
                // where it was, or the piped call behind it would find the sequence already half spent.
                File.WriteAllText(
                    path,
                    """{ "Version": 7, "Entries": [
                        { "Program": "tool", "Args": [], "HasStdin": false, "EnvNames": [], "EnvFingerprint": "1|default", "Stdout": "first", "Stderr": "", "Code": 0 },
                        { "Program": "tool", "Args": [], "HasStdin": false, "EnvNames": [], "EnvFingerprint": "1|default", "Stdout": "second", "Stderr": "", "Code": 0 }
                    ] }"""
                )

                match RecordReplayRunner.Replay path with
                | Error error -> Assert.Fail $"a v7 cassette must load: {error}"
                | Ok replayer ->
                    let silent = Command.create "tool" |> Command.stdout StdioMode.Null

                    for _ in 1..2 do
                        match! (runner replayer).OutputStringAsync(silent, CancellationToken.None) with
                        | Error(ProcessError.CassetteMiss "tool") -> ()
                        | other -> Assert.Fail $"a refused legacy candidate must stay a miss, got {other}"

                    let piped = Command.create "tool"

                    match! (runner replayer).OutputStringAsync(piped, CancellationToken.None) with
                    | Ok result ->
                        Assert.That(result.Stdout, Is.EqualTo "first", "the cursor must still be at entry 0")
                    | Error error -> Assert.Fail $"the first recording must still replay: {error}"

                    match! (runner replayer).OutputStringAsync(piped, CancellationToken.None) with
                    | Ok result -> Assert.That(result.Stdout, Is.EqualTo "second", "capture order must be intact")
                    | Error error -> Assert.Fail $"the second recording must still replay: {error}"
            })

    // --- Replayed output side effects (line handlers and tee sinks) -----------------------------------
    //
    // A replay serves the cassette instead of a child, but the CALLER's own output plumbing still has to
    // run: a command's `OnStdoutLine`/`OnStderrLine` handlers and its `StdoutTee`/`StderrTee` sinks are
    // what a progress parser or a log file is built on. Reconstructing a `ProcessResult` straight from an
    // entry touches no stream, so a hermetic replay used to skip all of them silently — turning exactly
    // the tests those callbacks exist for into no-ops, and swallowing a fault one of them raised along
    // with the rest. These tests pin what a replay hit reproduces, that it reproduces it exactly once,
    // that a handler/sink failure still surfaces, and that the record/miss path does not double up.

    [<Test>]
    member _.``a strict replay drives both line handlers, per stream and in line order``() : Task =
        withCassette (fun path ->
            task {
                File.WriteAllText(
                    path,
                    """{ "Version": 7, "Entries": [ { "Program": "tool", "Args": [], "HasStdin": false, "EnvNames": [], "EnvFingerprint": "1|default", "Stdout": "out1\nout2", "Stderr": "err1\nerr2", "Code": 0 } ] }"""
                )

                match RecordReplayRunner.Replay path with
                | Error error -> Assert.Fail $"replay load: {error}"
                | Ok replayer ->
                    use replayer = replayer
                    let stdoutLines = ResizeArray<string>()
                    let stderrLines = ResizeArray<string>()

                    let command =
                        Command.create "tool"
                        |> Command.onStdoutLine (fun line -> stdoutLines.Add line)
                        |> Command.onStderrLine (fun line -> stderrLines.Add line)

                    match! (runner replayer).OutputStringAsync(command, CancellationToken.None) with
                    | Ok result ->
                        CollectionAssert.AreEqual([| "out1"; "out2" |], stdoutLines.ToArray())
                        CollectionAssert.AreEqual([| "err1"; "err2" |], stderrLines.ToArray())
                        // The value handed back is still the one rebuilt from the entry: replaying the
                        // side effects must not become a second, disagreeing source of the result.
                        Assert.That(result.Stdout, Is.EqualTo "out1\nout2")
                        Assert.That(result.Stderr, Is.EqualTo "err1\nerr2")
                    | Error error -> Assert.Fail $"replay failed: {error}"
            })

    [<Test>]
    member _.``a strict replay feeds both tee sinks the exact recorded bytes``() : Task =
        withCassette (fun path ->
            task {
                File.WriteAllText(
                    path,
                    """{ "Version": 7, "Entries": [ { "Program": "tool", "Args": [], "HasStdin": false, "EnvNames": [], "EnvFingerprint": "1|default", "Stdout": "out1\nout2", "Stderr": "err1", "Code": 0 } ] }"""
                )

                match RecordReplayRunner.Replay path with
                | Error error -> Assert.Fail $"replay load: {error}"
                | Ok replayer ->
                    use replayer = replayer
                    use stdoutTee = new MemoryStream()
                    use stderrTee = new MemoryStream()

                    let command =
                        Command.create "tool"
                        |> Command.stdoutTee (stdoutTee :> Stream)
                        |> Command.stderrTee (stderrTee :> Stream)

                    match! (runner replayer).OutputStringAsync(command, CancellationToken.None) with
                    | Ok _ ->
                        // A tee is byte-exact and flushed by the pump itself, so the sink holds the
                        // recorded stream verbatim without the caller disposing it first.
                        Assert.That(
                            stdoutTee.ToArray(),
                            Is.EqualTo<byte>(Encoding.UTF8.GetBytes "out1\nout2"),
                            "stdout tee"
                        )

                        Assert.That(stderrTee.ToArray(), Is.EqualTo<byte>(Encoding.UTF8.GetBytes "err1"), "stderr tee")
                    | Error error -> Assert.Fail $"replay failed: {error}"
            })

    [<Test>]
    member _.``a replayed final line without a trailing newline is delivered exactly once``() : Task =
        task {
            // A recording normalizes its captured text, so both spellings occur in the wild: the same two
            // lines must come out of either, with the last one delivered once — never dropped for want of
            // a terminator, and never followed by a phantom empty line when one is present.
            let template =
                """{ "Version": 7, "Entries": [ { "Program": "tool", "Args": [], "HasStdin": false, "EnvNames": [], "EnvFingerprint": "1|default", "Stdout": "@STDOUT@", "Stderr": "", "Code": 0 } ] }"""

            let fixtures = [ "unterminated", "out1\\nout2"; "terminated", "out1\\nout2\\n" ]

            for label, recorded in fixtures do
                let path = Path.GetTempFileName()

                try
                    File.WriteAllText(path, template.Replace("@STDOUT@", recorded))

                    match RecordReplayRunner.Replay path with
                    | Error error -> Assert.Fail $"replay load ({label}): {error}"
                    | Ok replayer ->
                        use replayer = replayer
                        let lines = ResizeArray<string>()

                        let command =
                            Command.create "tool" |> Command.onStdoutLine (fun line -> lines.Add line)

                        match! (runner replayer).OutputStringAsync(command, CancellationToken.None) with
                        | Ok _ -> CollectionAssert.AreEqual([| "out1"; "out2" |], lines.ToArray(), label)
                        | Error error -> Assert.Fail $"replay failed ({label}): {error}"
                finally
                    deleteCassette path
        }

    [<Test>]
    member _.``a throwing line handler faults a replay instead of returning the recorded success``() : Task =
        withCassette (fun path ->
            task {
                File.WriteAllText(
                    path,
                    """{ "Version": 7, "Entries": [ { "Program": "tool", "Args": [], "HasStdin": false, "EnvNames": [], "EnvFingerprint": "1|default", "Stdout": "out1\nout2", "Stderr": "", "Code": 0 } ] }"""
                )

                match RecordReplayRunner.Replay path with
                | Error error -> Assert.Fail $"replay load: {error}"
                | Ok replayer ->
                    use replayer = replayer
                    let seen = ResizeArray<string>()

                    let command =
                        Command.create "tool"
                        |> Command.onStdoutLine (fun line ->
                            seen.Add line
                            raise (InvalidOperationException "handler blew up"))

                    let mutable caught: exn option = None

                    try
                        let! _ = (runner replayer).CaptureStringAsync(command, CancellationToken.None)
                        ()
                    with ex ->
                        // Captured rather than swallowed: the whole point is that the caller's own fault
                        // reaches them, so it is asserted on below instead of being ignored here.
                        caught <- Some ex

                    match caught with
                    | Some ex -> Assert.That(ex, Is.TypeOf<InvalidOperationException>())
                    | None -> Assert.Fail "a throwing handler must not be hidden behind the recorded success"

                    Assert.That(seen.Count, Is.EqualTo 1, "the handler must have run before it faulted")
            })

    [<Test>]
    member _.``a failing tee sink faults a replay as ProcessError.Io, like a live run``() : Task =
        withCassette (fun path ->
            task {
                File.WriteAllText(
                    path,
                    """{ "Version": 7, "Entries": [ { "Program": "tool", "Args": [], "HasStdin": false, "EnvNames": [], "EnvFingerprint": "1|default", "Stdout": "out1", "Stderr": "", "Code": 0 } ] }"""
                )

                match RecordReplayRunner.Replay path with
                | Error error -> Assert.Fail $"replay load: {error}"
                | Ok replayer ->
                    use replayer = replayer
                    use broken = new FailingTeeStream()

                    let command = Command.create "tool" |> Command.stdoutTee (broken :> Stream)
                    let mutable caught: exn option = None

                    try
                        let! _ = (runner replayer).CaptureStringAsync(command, CancellationToken.None)
                        ()
                    with ex ->
                        // Captured for the assertion below, not swallowed — a sink failure must not be
                        // able to end as a clean replayed success.
                        caught <- Some ex

                    match caught with
                    | Some(:? ProcessException as ex) ->
                        match ex.Error with
                        | ProcessError.Io _ -> ()
                        | other -> Assert.Fail $"a broken tee must surface as ProcessError.Io, got {other}"
                    | Some other -> Assert.Fail $"expected a ProcessException, got {other.GetType().Name}"
                    | None -> Assert.Fail "a broken tee sink must not be hidden behind the recorded success"
            })

    [<Test>]
    member _.``record mode does not re-run the side effects its inner runner already produced``() : Task =
        withCassette (fun path ->
            task {
                // `ScriptedRunner` builds its fake from the very command it is handed, so recording drives
                // the caller's handler through the real pumps exactly as a live run does. A record path
                // that ALSO replayed the entry it just wrote would double every line.
                let inner =
                    ScriptedRunner().Fallback(Reply.Ok("out1\nout2").WithStderr "err1") :> IProcessRunner

                let stdoutLines = ResizeArray<string>()
                let stderrLines = ResizeArray<string>()

                let command =
                    Command.create "tool"
                    |> Command.onStdoutLine (fun line -> stdoutLines.Add line)
                    |> Command.onStderrLine (fun line -> stderrLines.Add line)

                use recorder = RecordReplayRunner.Record(path, inner)

                match! (runner recorder).OutputStringAsync(command, CancellationToken.None) with
                | Ok _ -> ()
                | Error error -> Assert.Fail $"record failed: {error}"

                CollectionAssert.AreEqual([| "out1"; "out2" |], stdoutLines.ToArray(), "stdout handler")
                CollectionAssert.AreEqual([| "err1" |], stderrLines.ToArray(), "stderr handler")
            })

    [<Test>]
    member _.``an Auto miss produces its side effects once, and the following hit replays them once``() : Task =
        withCassette (fun path ->
            task {
                let inner = ScriptedRunner().Fallback(Reply.Ok "out1\nout2") :> IProcessRunner
                let lines = ResizeArray<string>()

                let command =
                    Command.create "tool" |> Command.onStdoutLine (fun line -> lines.Add line)

                match RecordReplayRunner.Auto(path, inner) with
                | Error error -> Assert.Fail $"auto load: {error}"
                | Ok recorder ->
                    use recorder = recorder

                    // The miss delegates to the inner runner, which produces the side effects itself.
                    match! (runner recorder).OutputStringAsync(command, CancellationToken.None) with
                    | Ok _ -> CollectionAssert.AreEqual([| "out1"; "out2" |], lines.ToArray(), "the miss")
                    | Error error -> Assert.Fail $"auto miss failed: {error}"

                    // The repeat hits the entry the miss just recorded, and replays the same lines once.
                    match! (runner recorder).OutputStringAsync(command, CancellationToken.None) with
                    | Ok _ ->
                        CollectionAssert.AreEqual(
                            [| "out1"; "out2"; "out1"; "out2" |],
                            lines.ToArray(),
                            "the hit must replay the lines exactly once more"
                        )
                    | Error error -> Assert.Fail $"auto hit failed: {error}"
            })

    [<Test>]
    member _.``a text recording tees its lines in the command's configured stdout encoding``() : Task =
        withCassette (fun path ->
            task {
                let command = Command.create "tool" |> Command.stdoutEncoding Encoding.Latin1

                do!
                    task {
                        use recorder =
                            RecordReplayRunner.Record(
                                path,
                                ScriptedRunner().Fallback(Reply.Ok "café") :> IProcessRunner
                            )

                        let! _ = (runner recorder).OutputStringAsync(command, CancellationToken.None)

                        match recorder.Save() with
                        | Ok() -> ()
                        | Error error -> Assert.Fail $"save: {error}"
                    }

                match RecordReplayRunner.Replay path with
                | Error error -> Assert.Fail $"replay load: {error}"
                | Ok replayer ->
                    use replayer = replayer
                    use tee = new MemoryStream()
                    let lines = ResizeArray<string>()

                    let observed =
                        command
                        |> Command.stdoutTee (tee :> Stream)
                        |> Command.onStdoutLine (fun line -> lines.Add line)

                    match! (runner replayer).OutputStringAsync(observed, CancellationToken.None) with
                    | Ok _ ->
                        CollectionAssert.AreEqual([| "café" |], lines.ToArray())
                        // A tee sees raw bytes, so it must see the command's own encoding — not UTF-8.
                        Assert.That(tee.ToArray(), Is.EqualTo<byte>(Encoding.Latin1.GetBytes "café"))
                    | Error error -> Assert.Fail $"replay failed: {error}"
            })

    [<Test>]
    member _.``a bytes replay reproduces the bytes verb's own side effects, not the text verb's``() : Task =
        withCassette (fun path ->
            task {
                // The bytes verb captures stdout RAW: its tee sees the bytes, but there is no line
                // structure, so `OnStdoutLine` is never called — while stderr stays line-pumped. A replay
                // must reproduce that asymmetry rather than inventing stdout lines, so the live double and
                // the cassette are driven through the same command shape and compared.
                let inner =
                    ScriptedRunner().Fallback(Reply.Ok("café").WithStderr "warn") :> IProcessRunner

                let observed
                    (stdoutLines: ResizeArray<string>)
                    (stderrLines: ResizeArray<string>)
                    (tee: MemoryStream)
                    =
                    Command.create "tool"
                    |> Command.stdoutEncoding Encoding.Latin1
                    |> Command.stdoutTee (tee :> Stream)
                    |> Command.onStdoutLine (fun line -> stdoutLines.Add line)
                    |> Command.onStderrLine (fun line -> stderrLines.Add line)

                let liveStdoutLines = ResizeArray<string>()
                let liveStderrLines = ResizeArray<string>()
                use liveTee = new MemoryStream()

                let! live =
                    inner.CaptureBytesAsync(observed liveStdoutLines liveStderrLines liveTee, CancellationToken.None)

                match live with
                | Ok _ -> ()
                | Error error -> Assert.Fail $"live bytes run failed: {error}"

                do!
                    task {
                        use recorder = RecordReplayRunner.Record(path, inner)

                        let! _ =
                            (runner recorder)
                                .OutputBytesAsync(
                                    Command.create "tool" |> Command.stdoutEncoding Encoding.Latin1,
                                    CancellationToken.None
                                )

                        match recorder.Save() with
                        | Ok() -> ()
                        | Error error -> Assert.Fail $"save: {error}"
                    }

                match RecordReplayRunner.Replay path with
                | Error error -> Assert.Fail $"replay load: {error}"
                | Ok replayer ->
                    use replayer = replayer
                    let stdoutLines = ResizeArray<string>()
                    let stderrLines = ResizeArray<string>()
                    use tee = new MemoryStream()

                    match!
                        (runner replayer)
                            .OutputBytesAsync(observed stdoutLines stderrLines tee, CancellationToken.None)
                    with
                    | Ok _ ->
                        Assert.That(stdoutLines, Is.Empty, "the bytes verb has no stdout line structure")
                        CollectionAssert.AreEqual(liveStdoutLines.ToArray(), stdoutLines.ToArray(), "stdout handler")
                        CollectionAssert.AreEqual([| "warn" |], stderrLines.ToArray(), "stderr handler")
                        CollectionAssert.AreEqual(liveStderrLines.ToArray(), stderrLines.ToArray(), "stderr handler")
                        Assert.That(tee.ToArray(), Is.EqualTo<byte>(liveTee.ToArray()), "stdout tee")
                        Assert.That(tee.ToArray(), Is.EqualTo<byte>(Encoding.Latin1.GetBytes "café"), "stdout tee")
                    | Error error -> Assert.Fail $"replay failed: {error}"
            })

    [<Test>]
    member _.``a bytes recording tees its exact non-UTF-8 bytes on replay and through a spawn``() : Task =
        withCassette (fun path ->
            task {
                // Bytes that no encoding round-trip survives (a lone 0xFF, an embedded NUL): the tee must
                // get the recorded bytes themselves, not a decode-then-re-encode that replaces them with
                // U+FFFD — that loss is precisely what the bytes capture verb exists to avoid.
                let raw = [| 0xFFuy; 0xFEuy; 0x00uy; 0x01uy; 0x80uy; 0x41uy |]

                do!
                    task {
                        use recorder = RecordReplayRunner.Record(path, FixedBytesRunner(raw, "", 0))
                        let! _ = (runner recorder).OutputBytesAsync(Command.create "tool", CancellationToken.None)

                        match recorder.Save() with
                        | Ok() -> ()
                        | Error error -> Assert.Fail $"save: {error}"
                    }

                match RecordReplayRunner.Replay path with
                | Error error -> Assert.Fail $"replay load: {error}"
                | Ok replayer ->
                    use replayer = replayer
                    use directTee = new MemoryStream()

                    match!
                        (runner replayer)
                            .OutputBytesAsync(
                                Command.create "tool" |> Command.stdoutTee (directTee :> Stream),
                                CancellationToken.None
                            )
                    with
                    | Ok result ->
                        Assert.That(result.Stdout, Is.EqualTo<byte>(raw), "the replayed result")
                        Assert.That(directTee.ToArray(), Is.EqualTo<byte>(raw), "the direct capture's tee")
                    | Error error -> Assert.Fail $"bytes replay failed: {error}"

                    // The reconstructed handle carries the same bytes, so a streaming consumer of a
                    // replayed spawn cannot see something the direct capture verb did not.
                    use spawnTee = new MemoryStream()

                    match!
                        (runner replayer)
                            .SpawnAsync(
                                Command.create "tool" |> Command.stdoutTee (spawnTee :> Stream),
                                CancellationToken.None
                            )
                    with
                    | Error error -> Assert.Fail $"spawn replay failed: {error}"
                    | Ok proc ->
                        use proc = proc

                        match! proc.OutputBytesAsync() with
                        | Ok result ->
                            Assert.That(result.Stdout, Is.EqualTo<byte>(raw), "the spawned handle's own bytes")
                            Assert.That(spawnTee.ToArray(), Is.EqualTo<byte>(raw), "the spawn replay's tee")
                        | Error error -> Assert.Fail $"spawned bytes capture failed: {error}"
            })

    [<Test>]
    member _.``a PTY replay drives the merged stream and has no separate stderr side effect``() : Task =
        withCassette (fun path ->
            task {
                // A PTY is one terminal device, so the builder refuses a separate-stderr observer outright
                // — there is no such stream for a replay to drive, and the recorded merged output has to
                // reach the STDOUT handler and tee whole.
                let ptyCommand = Command.create "tui" |> Command.pty

                Assert.Throws<ArgumentException>(
                    Action(fun () -> ptyCommand.OnStderrLine(Action<string>(fun _ -> ())) |> ignore)
                )
                |> ignore

                Assert.Throws<ArgumentException>(Action(fun () -> ptyCommand.StderrTee(new MemoryStream()) |> ignore))
                |> ignore

                do!
                    task {
                        use recorder =
                            RecordReplayRunner.Record(
                                path,
                                ScriptedRunner().Fallback(Reply.Ok("frame1\nframe2").WithStderr "warn")
                                :> IProcessRunner
                            )

                        let! _ = (runner recorder).OutputStringAsync(ptyCommand, CancellationToken.None)

                        match recorder.Save() with
                        | Ok() -> ()
                        | Error error -> Assert.Fail $"save: {error}"
                    }

                match RecordReplayRunner.Replay path with
                | Error error -> Assert.Fail $"replay load: {error}"
                | Ok replayer ->
                    use replayer = replayer
                    use tee = new MemoryStream()
                    let lines = ResizeArray<string>()

                    let observed =
                        ptyCommand
                        |> Command.stdoutTee (tee :> Stream)
                        |> Command.onStdoutLine (fun line -> lines.Add line)

                    match! (runner replayer).OutputStringAsync(observed, CancellationToken.None) with
                    | Ok result ->
                        // The scripted stderr was folded into the terminal stream at record time, so it
                        // arrives as another stdout line — never as a separate stderr observation.
                        CollectionAssert.AreEqual([| "frame1"; "frame2"; "warn" |], lines.ToArray())
                        Assert.That(tee.ToArray(), Is.EqualTo<byte>(Encoding.UTF8.GetBytes "frame1\nframe2\nwarn"))
                        Assert.That(result.Stderr, Is.EqualTo "", "a PTY result carries no separate stderr")
                    | Error error -> Assert.Fail $"replay failed: {error}"
            })

    [<Test>]
    member _.``a direct replay and a SpawnAsync replay produce the same side effects, PTY included``() : Task =
        task {
            for label, isPty in [ "plain", false; "pty", true ] do
                let path = Path.GetTempFileName()

                try
                    let bare =
                        if isPty then
                            Command.create "tool" |> Command.pty
                        else
                            Command.create "tool"

                    let observed (lines: ResizeArray<string>) (tee: MemoryStream) =
                        bare
                        |> Command.stdoutTee (tee :> Stream)
                        |> Command.onStdoutLine (fun line -> lines.Add line)

                    do!
                        task {
                            use recorder =
                                RecordReplayRunner.Record(
                                    path,
                                    ScriptedRunner().Fallback(Reply.Ok "out1\nout2") :> IProcessRunner
                                )

                            let! _ = (runner recorder).OutputStringAsync(bare, CancellationToken.None)

                            match recorder.Save() with
                            | Ok() -> ()
                            | Error error -> Assert.Fail $"save ({label}): {error}"
                        }

                    match RecordReplayRunner.Replay path with
                    | Error error -> Assert.Fail $"replay load ({label}): {error}"
                    | Ok replayer ->
                        use replayer = replayer
                        let directLines = ResizeArray<string>()
                        use directTee = new MemoryStream()

                        match!
                            (runner replayer).OutputStringAsync(observed directLines directTee, CancellationToken.None)
                        with
                        | Ok _ -> ()
                        | Error error -> Assert.Fail $"direct replay ({label}): {error}"

                        // The entry repeats once its capture order is exhausted, so the same recording
                        // serves the spawned replay below.
                        let spawnLines = ResizeArray<string>()
                        use spawnTee = new MemoryStream()

                        match! (runner replayer).SpawnAsync(observed spawnLines spawnTee, CancellationToken.None) with
                        | Error error -> Assert.Fail $"spawn replay ({label}): {error}"
                        | Ok proc ->
                            use proc = proc

                            match! proc.OutputStringAsync() with
                            | Ok _ -> ()
                            | Error error -> Assert.Fail $"spawned capture ({label}): {error}"

                        CollectionAssert.AreEqual([| "out1"; "out2" |], directLines.ToArray(), $"{label} direct lines")
                        CollectionAssert.AreEqual(directLines.ToArray(), spawnLines.ToArray(), $"{label} lines agree")
                        Assert.That(spawnTee.ToArray(), Is.EqualTo<byte>(directTee.ToArray()), $"{label} tees agree")
                finally
                    deleteCassette path
        }
