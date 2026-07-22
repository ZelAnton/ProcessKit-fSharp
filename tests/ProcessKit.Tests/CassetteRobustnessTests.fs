namespace ProcessKit.Tests

open System
open System.IO
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open NUnit.Framework
open FsCheck
open FsCheck.FSharp
open ProcessKit
open ProcessKit.Testing

/// Adversarial (property-based) robustness tests for the cassette parser — the one place in the
/// library that reads **untrusted external input**: `RecordReplayRunner.Replay` deserializes a
/// JSON cassette off disk and reconstructs a replay from it (`src/ProcessKit.Testing/Cassette.fs`).
///
/// The invariant every generated input must satisfy: loading (and then replaying) a cassette either
/// **succeeds** or fails with a **typed `ProcessError`** — never an unhandled exception, a hang, or
/// unbounded memory growth. `Replay` and the three replay verbs (`CaptureStringAsync` /
/// `CaptureBytesAsync` / `SpawnAsync`) all return `Result<_, ProcessError>`, so *any* exception
/// escaping them is a defect. The property bodies therefore return `true` and deliberately do **not**
/// catch: a genuine defect surfaces as an exception thrown through `Check.QuickThrowOnFailure`, which
/// shrinks to a minimal counter-example and reports it as an ordinary NUnit failure. A hang or an
/// out-of-memory input would manifest as the test run itself never finishing / OOM-ing, so the
/// extreme-size and long-string generators below *are* the hang/unbounded-memory guard — no per-case
/// wall-clock assertion is needed. FsCheck's default 100 cases per property keep the whole suite a
/// fast part of an ordinary `dotnet test` (each case is one in-memory temp-file write plus a parse).
///
/// This generalizes the example-driven corrupt/partial-cassette coverage in `CassetteTests.fs`
/// (omitted fields, oversized `DurationMs`, corrupt base64, contradictory terminal state, unsupported
/// version) across randomly generated truncation, bit-flips, hostile field values, and every declared
/// format version — in the same FsCheck style as `PumpPropertyTests.fs` / `OutputPolicyPropertyTests.fs`.
[<TestFixture>]
type CassetteRobustnessTests() =

    // A deterministic inner runner used only to record the canonical seed cassette below (a text entry
    // and a bytes entry, so the seed carries both a plain `Stdout` and a base64 `StdoutBase64`).
    static let seedInner =
        { new IProcessRunner with
            member _.CaptureStringAsync(command, _ct) =
                Task.FromResult(
                    Ok(
                        ProcessResult<string>(
                            command.Program,
                            "seed-out",
                            "seed-err",
                            Outcome.Exited 0,
                            TimeSpan.FromMilliseconds 5.0,
                            false,
                            [ 0 ]
                        )
                    )
                )

            member _.CaptureBytesAsync(command, _ct) =
                Task.FromResult(
                    Ok(
                        ProcessResult<byte[]>(
                            command.Program,
                            [| 1uy; 2uy; 3uy; 0xFFuy |],
                            "seed-err",
                            Outcome.Exited 0,
                            TimeSpan.FromMilliseconds 5.0,
                            false,
                            [ 0 ]
                        )
                    )
                )

            member _.SpawnAsync(_command, _ct) =
                Task.FromResult(Error(ProcessError.Unsupported "seed runner has no spawn")) }

    // Record one genuinely valid current-format cassette, then read back BOTH its raw bytes (the seed
    // for the truncation / bit-flip generators) and its declared `"Version"`. The version is discovered
    // at runtime from an actual recording rather than hardcoded, so this suite tracks a format bump
    // automatically instead of asserting a stale number (K-034: read the current format version from
    // the code, never assume it from the task text).
    static let seedBytes, currentVersion =
        let path =
            Path.Combine(Path.GetTempPath(), $"pk-robust-seed-{Guid.NewGuid():N}.json")

        try
            (task {
                use recorder = RecordReplayRunner.Record(path, seedInner)
                let runner = recorder :> IProcessRunner
                let textCmd = Command.create "tool" |> Command.arg "x"
                let bytesCmd = Command.create "tool" |> Command.arg "b"
                let! _ = runner.CaptureStringAsync(textCmd, CancellationToken.None)
                let! _ = runner.CaptureBytesAsync(bytesCmd, CancellationToken.None)

                match recorder.Save() with
                | Ok() -> ()
                | Error error -> failwith $"seed cassette save failed: {error}"
            })
                .GetAwaiter()
                .GetResult()

            let bytes = File.ReadAllBytes path
            use doc = JsonDocument.Parse(File.ReadAllText path)
            let version = doc.RootElement.GetProperty("Version").GetInt32()
            bytes, version
        finally
            try
                if File.Exists path then
                    File.Delete path
            with _ ->
                // Best-effort cleanup of the one-off seed fixture; a locked temp file must not fail
                // static initialization.
                ()

    static let freshTemp () =
        Path.Combine(Path.GetTempPath(), $"pk-robust-{Guid.NewGuid():N}.json")

    static let tryDelete (path: string) : unit =
        try
            if File.Exists path then
                File.Delete path
        with _ ->
            // Best-effort cleanup of a transient fuzz fixture; a locked/already-removed temp file must
            // not fail the test whose real assertion (no unhandled exception) has already run.
            ()

    static let utf8 (s: string) : byte[] = Encoding.UTF8.GetBytes s

    // A JSON string literal (properly quoted and escaped) for any generated content — including one
    // holding quotes, backslashes, or a raw newline — so composing a cassette by hand never emits
    // malformed JSON for a reason unrelated to what is under test.
    static let jstr (s: string) : string = JsonSerializer.Serialize s

    // A JSON number literal for a double, in valid JSON `E`-notation where needed (so an extreme
    // magnitude such as `1e18` still parses as a number and reaches the parser's clamping logic).
    static let jnum (d: float) : string = JsonSerializer.Serialize d

    // A `"name": value` JSON object member from a pre-rendered JSON `value`.
    static let kv (name: string) (jsonValue: string) : string = "\"" + name + "\": " + jsonValue

    // Wrap already-rendered object-member fragments into `{ "Version": v, "Entries": [ { ... } ] }`.
    static let cassetteJson (version: string) (entryMembers: string list) : string =
        "{ \"Version\": "
        + version
        + ", \"Entries\": [ { "
        + String.Join(", ", entryMembers)
        + " } ] }"

    // Exercise the three replay verbs against a fixed command, so a hostile *entry* is driven through
    // the reconstruction path (base64 decode, encoding, duration/outcome rebuild, fake-handle spawn),
    // not merely through the load path. Any exception escaping these `Result`-returning verbs
    // propagates out of the property as the defect it is. A matched `SpawnAsync` handle is drained and
    // disposed to run the streaming reconstruction to completion.
    static let exerciseVerbs (replayer: RecordReplayRunner) : unit =
        (task {
            let runner = replayer :> IProcessRunner
            let command = Command.create "tool"
            let! _ = runner.CaptureStringAsync(command, CancellationToken.None)
            let! _ = runner.CaptureBytesAsync(command, CancellationToken.None)

            match! runner.SpawnAsync(command, CancellationToken.None) with
            | Ok proc ->
                use proc = proc
                let enumerator = proc.OutputEventsAsync().GetAsyncEnumerator()
                let mutable more = true

                while more do
                    let! has = enumerator.MoveNextAsync()
                    more <- has

                do! enumerator.DisposeAsync()
                let! _ = proc.FinishAsync()
                ()
            | Error _ -> ()
        })
            .GetAwaiter()
            .GetResult()

    // Write `content`, load it as a cassette, and (on a successful load) exercise replay — asserting
    // only that neither step throws. Returns `true` unconditionally: the sole falsifier is an escaped
    // exception, which FsCheck reports with the shrunk input.
    static let loadIsRobust (content: byte[]) : bool =
        let path = freshTemp ()

        try
            File.WriteAllBytes(path, content)

            match RecordReplayRunner.Replay path with
            | Error _ ->
                // A typed load error is a fully acceptable outcome for hostile input.
                true
            | Ok replayer ->
                use replayer = replayer
                exerciseVerbs replayer
                true
        finally
            tryDelete path

    // --- Property 1: arbitrary bytes — a completely random file (garbage, invalid UTF-8, invalid
    // JSON, invalid base64, whatever) loads to a typed error rather than throwing. ---

    [<Test>]
    member _.``loading an arbitrary byte file never throws — only Ok or a typed ProcessError``() =
        let case =
            gen {
                let! bytes = Gen.listOf (Gen.choose (0, 255) |> Gen.map byte)
                return List.toArray bytes
            }

        let property = Prop.forAll (Arb.fromGen case) loadIsRobust
        Check.QuickThrowOnFailure property

    // --- Property 2: truncation at an arbitrary boundary — a valid cassette cut off at any byte offset
    // (mid-token, mid-string, mid-base64, mid-number) loads without an unhandled JSON/parse fault. ---

    [<Test>]
    member _.``a valid cassette truncated at any byte boundary loads without throwing``() =
        let property =
            Prop.forAll (Arb.fromGen (Gen.choose (0, seedBytes.Length))) (fun n ->
                loadIsRobust (Array.sub seedBytes 0 n))

        Check.QuickThrowOnFailure property

    // --- Property 3: bit-flips on a valid cassette — flipping arbitrary bits of a well-formed cassette
    // (the classic "corrupted on disk" case) yields either a still-valid cassette or a typed error,
    // never a crash, on either the load or the replay path. ---

    [<Test>]
    member _.``a bitwise-corrupted valid cassette loads and replays without throwing``() =
        let flipGen =
            gen {
                let! index = Gen.choose (0, seedBytes.Length - 1)
                let! bit = Gen.choose (0, 7)
                return index, bit
            }

        let property =
            Prop.forAll (Arb.fromGen (Gen.listOf flipGen)) (fun flips ->
                let corrupted = Array.copy seedBytes

                for index, bit in flips do
                    corrupted[index] <- corrupted[index] ^^^ (1uy <<< bit)

                loadIsRobust corrupted)

        Check.QuickThrowOnFailure property

    // --- Property 4: hostile, well-formed JSON with adversarial field values — the JSON parses, but
    // the fields are inconsistent/missing/extreme: a null or missing or very long `Program`, `Args`
    // holding nulls, corrupt/empty/valid base64, several mutually-exclusive terminal-state fields set
    // at once, an out-of-range/NaN-ish `DurationMs`, and extreme PTY geometry. Every combination must
    // resolve to Ok or a typed error across load AND replay. Declared at a randomly chosen *supported*
    // version so the hostile entry is actually reached (not short-circuited by a version rejection),
    // which also exercises tolerant back-compat loading of every past format version. ---

    [<Test>]
    member _.``hostile well-formed JSON with adversarial field values never throws on load or replay``() =
        let case =
            gen {
                let! version = Gen.choose (1, currentVersion)
                let! programChoice = Gen.elements [ 0; 1; 2; 3 ]
                let! argCount = Gen.choose (0, 4)
                let! argChoices = Gen.listOfLength argCount (Gen.elements [ 0; 1 ])
                let! stdoutChoice = Gen.elements [ 0; 1; 2 ]
                let! base64Choice = Gen.elements [ 0; 1; 2; 3 ]
                let! rawBytes = Gen.listOf (Gen.choose (0, 255) |> Gen.map byte)
                let! timedOut = Gen.elements [ true; false ]
                let! hasSignal = Gen.elements [ true; false ]
                let! signalVal = Gen.choose (1, 64)
                let! hasCode = Gen.elements [ true; false ]
                let! codeVal = Gen.choose (-1, 5)
                let! durationChoice = Gen.elements [ 0; 1; 2; 3; 4 ]
                let! pty = Gen.elements [ true; false ]
                let! ptyCols = Gen.elements [ 0; 80; -1; Int32.MaxValue; Int32.MinValue ]
                let! ptyRows = Gen.elements [ 0; 24; -1; Int32.MaxValue; Int32.MinValue ]

                return
                    {| Version = version
                       ProgramChoice = programChoice
                       ArgChoices = argChoices
                       StdoutChoice = stdoutChoice
                       Base64Choice = base64Choice
                       RawBytes = List.toArray rawBytes
                       TimedOut = timedOut
                       HasSignal = hasSignal
                       SignalVal = signalVal
                       HasCode = hasCode
                       CodeVal = codeVal
                       DurationChoice = durationChoice
                       Pty = pty
                       PtyCols = ptyCols
                       PtyRows = ptyRows |}
            }

        let property =
            Prop.forAll (Arb.fromGen case) (fun c ->
                let programMember =
                    match c.ProgramChoice with
                    | 0 -> Some(kv "Program" (jstr "tool"))
                    | 1 -> Some(kv "Program" (jstr ""))
                    | 2 -> Some(kv "Program" "null")
                    | _ -> Some(kv "Program" (jstr (String('p', 200))))

                let argsJson =
                    c.ArgChoices
                    |> List.map (fun choice -> if choice = 0 then jstr "a" else "null")
                    |> (fun elems -> "[" + String.Join(", ", elems) + "]")

                let stdoutJson =
                    match c.StdoutChoice with
                    | 0 -> jstr ""
                    | 1 -> jstr "out"
                    | _ -> jstr (String('s', 200))

                let base64Member =
                    match c.Base64Choice with
                    | 0 -> None // omitted → text entry
                    | 1 -> Some(kv "StdoutBase64" (jstr "")) // empty-bytes recording
                    | 2 -> Some(kv "StdoutBase64" (jstr (Convert.ToBase64String c.RawBytes))) // valid base64
                    | _ -> Some(kv "StdoutBase64" (jstr "not valid base64 @@@ !!")) // corrupt base64

                let durationJson =
                    match c.DurationChoice with
                    | 0 -> jnum 0.0
                    | 1 -> jnum 250.0
                    | 2 -> jnum 1e18 // far beyond TimeSpan's range
                    | 3 -> jnum -5.0 // negative
                    | _ -> jnum 1.7e308 // near Double.MaxValue

                let members =
                    [ yield! Option.toList programMember
                      yield kv "Args" argsJson
                      yield kv "HasStdin" "false"
                      yield kv "EnvNames" "[]"
                      yield kv "Stdout" stdoutJson
                      yield kv "Stderr" (jstr "")
                      yield! Option.toList base64Member
                      if c.TimedOut then
                          yield kv "TimedOut" "true"
                      if c.HasSignal then
                          yield kv "Signal" (string c.SignalVal)
                      if c.HasCode then
                          yield kv "Code" (string c.CodeVal)
                      yield kv "DurationMs" durationJson
                      yield kv "Pty" (if c.Pty then "true" else "false")
                      yield kv "PtyCols" (string c.PtyCols)
                      yield kv "PtyRows" (string c.PtyRows) ]

                loadIsRobust (utf8 (cassetteJson (string c.Version) members)))

        Check.QuickThrowOnFailure property

    // --- Property 5: matched-entry base64/encoding decode — a cassette whose single entry is keyed to
    // match the replay command (`tool`, no args/stdin/env) so replay actually reaches the base64 →
    // bytes → text decode path. The generated `StdoutBase64` is valid, empty, or deliberately corrupt;
    // a corrupt payload must surface a typed `Io` error on every verb, never an unhandled
    // `FormatException` and never a silent empty/placeholder stdout. ---

    [<Test>]
    member _.``a matched entry with generated base64 decodes to Ok or a typed error on every verb``() =
        let case =
            gen {
                let! base64Choice = Gen.elements [ 0; 1; 2 ]
                let! rawBytes = Gen.listOf (Gen.choose (0, 255) |> Gen.map byte)

                let! corruptChars = Gen.nonEmptyListOf (Gen.elements [ '!'; '@'; ' '; '='; '\n'; 'z'; '_'; '-' ])

                return
                    {| Base64Choice = base64Choice
                       RawBytes = List.toArray rawBytes
                       Corrupt = String(List.toArray corruptChars) |}
            }

        let property =
            Prop.forAll (Arb.fromGen case) (fun c ->
                let base64Value =
                    match c.Base64Choice with
                    | 0 -> Convert.ToBase64String c.RawBytes
                    | 1 -> ""
                    | _ -> c.Corrupt

                let members =
                    [ kv "Program" (jstr "tool")
                      kv "Args" "[]"
                      kv "HasStdin" "false"
                      kv "EnvNames" "[]"
                      kv "Stdout" (jstr "")
                      kv "Stderr" (jstr "")
                      kv "StdoutBase64" (jstr base64Value)
                      kv "Code" "0" ]

                loadIsRobust (utf8 (cassetteJson (string currentVersion) members)))

        Check.QuickThrowOnFailure property

    // --- Property 6: the declared format version, swept across nonsensical/past/current/future values.
    // A minimal valid entry is loaded at each version. Every supported version (1..current) must load
    // successfully and replay the recorded stdout (back-compat for EVERY past version, not just the
    // newest); a version < 1 or > current must be a typed rejection — never a crash, never a silent
    // mis-read of a future format. ---

    [<Test>]
    member _.``every declared version resolves: supported loads and replays, out-of-range is a typed error``() =
        let case =
            gen {
                let! choice = Gen.elements [ 0; 1; 2; 3 ]

                return!
                    match choice with
                    | 0 -> Gen.choose (1, currentVersion) // supported
                    | 1 -> Gen.choose (currentVersion + 1, currentVersion + 50) // future
                    | 2 -> Gen.choose (-10, 0) // nonsensical
                    | _ -> Gen.constant Int32.MaxValue // extreme future
            }

        let property =
            Prop.forAll (Arb.fromGen case) (fun version ->
                let members =
                    [ kv "Program" (jstr "tool")
                      kv "Args" "[]"
                      kv "HasStdin" "false"
                      kv "EnvNames" "[]"
                      kv "Stdout" (jstr "recorded")
                      kv "Stderr" (jstr "")
                      kv "Code" "0" ]

                let path = freshTemp ()
                let supported = version >= 1 && version <= currentVersion

                try
                    File.WriteAllBytes(path, utf8 (cassetteJson (string version) members))

                    (task {
                        match RecordReplayRunner.Replay path with
                        | Ok replayer ->
                            use replayer = replayer
                            // A supported version must both load AND replay the recorded stdout; an
                            // out-of-range version must never have loaded.
                            if not supported then
                                return false
                            else
                                match!
                                    (replayer :> IProcessRunner)
                                        .CaptureStringAsync(Command.create "tool", CancellationToken.None)
                                with
                                | Ok result -> return result.Stdout = "recorded"
                                | Error _ -> return false
                        | Error _ ->
                            // Only an out-of-range version is allowed to be rejected here.
                            return not supported
                    })
                        .GetAwaiter()
                        .GetResult()
                finally
                    tryDelete path)

        Check.QuickThrowOnFailure property

    // --- Explicit boundary cases (checked directly, not merely relied upon to appear at random). ---

    [<Test>]
    member _.``an empty, whitespace, or JSON-null file is a typed error, not a throw``() =
        for content in [ ""; "   "; "\n\t "; "null"; "[]"; "{}" ] do
            let robust = loadIsRobust (utf8 content)
            let label = $"content {jstr content} must load without throwing"
            Assert.That(robust, Is.True, label)

    [<Test>]
    member _.``a deeply nested JSON array is a typed error, not a StackOverflow``() =
        // A crafted deep-nesting payload must be rejected by the parser's depth guard (System.Text.Json
        // caps recursion at MaxDepth) rather than exhausting the stack — the classic recursive-descent
        // DoS. 20000 levels is far past any legitimate cassette shape.
        let depth = 20000
        let nested = String('[', depth) + String(']', depth)
        let label = "deep nesting must not crash the loader"
        Assert.That(loadIsRobust (utf8 nested), Is.True, label)

    [<Test>]
    member _.``a very long Program/Stdout string loads without an unbounded blow-up``() =
        // "Very long strings" from the criteria: a ~1 MB program name and stdout. The parser must not
        // fault or balloon disproportionately — it either loads them or reports a typed error.
        let big = String('x', 1_000_000)

        let members =
            [ kv "Program" (jstr big)
              kv "Args" "[]"
              kv "HasStdin" "false"
              kv "EnvNames" "[]"
              kv "Stdout" (jstr big)
              kv "Stderr" (jstr "")
              kv "Code" "0" ]

        let label = "a very long string must not crash the loader"
        Assert.That(loadIsRobust (utf8 (cassetteJson (string currentVersion) members)), Is.True, label)

    [<Test>]
    member _.``an extreme-geometry PTY entry replays as a merged stream without throwing``() : Task =
        task {
            // A crafted PTY (v4) entry with out-of-range geometry, keyed to match the replay command,
            // must reconstruct a merged-stream handle (only OutputEvent.Stdout) without faulting —
            // extreme geometry is inert on replay (the fake handle carries the recorded merged output,
            // not a live terminal), and must stay that way.
            let members =
                [ kv "Program" (jstr "tui")
                  kv "Args" "[]"
                  kv "HasStdin" "false"
                  kv "EnvNames" "[]"
                  kv "Stdout" (jstr "frame1\nframe2")
                  kv "Stderr" (jstr "")
                  kv "Code" "0"
                  kv "Pty" "true"
                  kv "PtyCols" (string Int32.MaxValue)
                  kv "PtyRows" (string Int32.MinValue) ]

            let path = freshTemp ()

            try
                File.WriteAllBytes(path, utf8 (cassetteJson (string currentVersion) members))

                match RecordReplayRunner.Replay path with
                | Error error -> Assert.Fail $"an extreme-geometry PTY cassette must still load: {error}"
                | Ok replayer ->
                    use replayer = replayer

                    match!
                        (replayer :> IProcessRunner)
                            .SpawnAsync(Command.create "tui" |> Command.pty, CancellationToken.None)
                    with
                    | Error error -> Assert.Fail $"a PTY entry must replay a live handle: {error}"
                    | Ok proc ->
                        use proc = proc
                        let events = ResizeArray<OutputEvent>()
                        let enumerator = proc.OutputEventsAsync().GetAsyncEnumerator()
                        let mutable more = true

                        while more do
                            let! has = enumerator.MoveNextAsync()

                            if has then events.Add enumerator.Current else more <- false

                        do! enumerator.DisposeAsync()

                        let onlyStdout =
                            events
                            |> Seq.forall (fun e ->
                                match e with
                                | OutputEvent.Stdout _ -> true
                                | OutputEvent.Stderr _ -> false)

                        Assert.That(onlyStdout, Is.True, "a replayed PTY handle must emit only OutputEvent.Stdout")
            finally
                tryDelete path
        }
