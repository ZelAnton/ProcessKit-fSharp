namespace ProcessKit.Tests

open System
open System.Collections.Generic
open System.IO
open System.Runtime.InteropServices
open System.Text
open System.Threading
open System.Threading.Tasks
open NUnit.Framework
open NUnit.Framework.Legacy
open ProcessKit
open ProcessKit.Testing

/// A deterministic inner `IProcessRunner` whose bytes verb returns arbitrary (possibly non-UTF-8)
/// `stdout` bytes, for record-mode bytes tests. The string/spawn verbs are unused here.
type private FixedBytesRunner(stdout: byte[], stderr: string, code: int) =
    interface IProcessRunner with
        member _.CaptureBytesAsync(command, _cancellationToken) =
            Task.FromResult(
                Ok(
                    ProcessResult<byte[]>(
                        command.Program,
                        stdout,
                        stderr,
                        Outcome.Exited code,
                        TimeSpan.Zero,
                        false,
                        [ 0 ]
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
                        TimeSpan.Zero,
                        false,
                        [ 0 ]
                    )
                )
            )

        member _.SpawnAsync(_command, _cancellationToken) =
            Task.FromResult(Error(ProcessError.Unsupported "FixedBytesRunner has no Spawn"))

/// A deterministic inner `IProcessRunner` for record-mode tests: every call returns `stdout`/`code`.
type private FixedRunner(stdout: string, code: int) =
    interface IProcessRunner with
        member _.CaptureStringAsync(command, _cancellationToken) =
            Task.FromResult(
                Ok(ProcessResult<string>(command.Program, stdout, "", Outcome.Exited code, TimeSpan.Zero, false, [ 0 ]))
            )

        member _.CaptureBytesAsync(command, _cancellationToken) =
            Task.FromResult(
                Ok(
                    ProcessResult<byte[]>(
                        command.Program,
                        Encoding.UTF8.GetBytes stdout,
                        "",
                        Outcome.Exited code,
                        TimeSpan.Zero,
                        false,
                        [ 0 ]
                    )
                )
            )

        member _.SpawnAsync(_command, _cancellationToken) =
            Task.FromResult(Error(ProcessError.Unsupported "FixedRunner has no Spawn"))

[<TestFixture>]
type CassetteTests() =

    let withCassette (body: string -> Task) : Task =
        task {
            let path = Path.GetTempFileName()

            try
                do! body path
            finally
                if File.Exists path then
                    File.Delete path
        }

    let runner (r: RecordReplayRunner) : IProcessRunner = r

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
                if File.Exists path then
                    File.Delete path
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
    member _.``Dispose flushes the cassette; the bytes verb is rejected``() : Task =
        withCassette (fun path ->
            task {
                let command = Command.create "tool" |> Command.arg "z"

                // No explicit Save — the drop-time flush must persist the recording.
                do!
                    task {
                        use recorder = RecordReplayRunner.Record(path, FixedRunner("byte-output", 0))
                        let! _ = (runner recorder).OutputStringAsync(command, CancellationToken.None)
                        ()
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
                        ()
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
                        ()
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
                        ()
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
                        ()
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
                        ()
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
                        ()
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
                        ()
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
                        ()
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
                        ()
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
                        ()
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

    // --- PTY recordings (Stage 5 — D3 merged stream, v4 schema bump, back-compat load) -------------

    [<Test>]
    member _.``recording a Command.Pty run writes a v4 cassette with the Pty flag and geometry``() : Task =
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
                Assert.That(onDisk, Does.Contain "\"Version\": 4", "a PTY recording writes the v4 format")
                Assert.That(onDisk, Does.Contain "\"Pty\": true")
                // PtyConfig.Default geometry is 80x24.
                Assert.That(onDisk, Does.Contain "\"PtyCols\": 80")
                Assert.That(onDisk, Does.Contain "\"PtyRows\": 24")
            })

    [<Test>]
    member _.``pre-v4 cassettes v1 v2 and v3 still load and replay as non-PTY under the v4 build``() : Task =
        task {
            // One hand-crafted fixture per legacy version. Each must load under the v4 build (a missing
            // Pty field defaults to false / non-PTY) and replay its recorded stdout, proving the v1→v4
            // back-compat load path, not just v1/v2.
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
                  """{ "Version": 3, "Entries": [ { "Program": "legacy3", "Args": [], "HasStdin": false, "EnvNames": [], "EnvFingerprint": "1|default", "Stdout": "three", "Stderr": "" } ] }""" ]

            for version, program, expected, json in fixtures do
                let path = Path.GetTempFileName()

                try
                    File.WriteAllText(path, json)

                    match RecordReplayRunner.Replay path with
                    | Error error -> Assert.Fail $"a v{version} cassette must still load under the v4 build: {error}"
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
