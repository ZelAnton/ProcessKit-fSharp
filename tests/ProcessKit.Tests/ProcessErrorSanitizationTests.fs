namespace ProcessKit.Tests

open System
open NUnit.Framework
open ProcessKit

/// The display-safety contract of the human-readable failure render — `ProcessError.Message`,
/// `ProcessError.ToString()`, and the `ProcessException.Message` they become (src/ProcessKit/
/// ProcessError.fs, src/ProcessKit/ProcessException.fs).
///
/// A failure's text routinely carries bytes ProcessKit did not author: a hostile child's stderr, a
/// JSON-RPC peer's own error text, a caller's parser message, an OS error string built around a
/// caller-supplied path. The documented consumer habit is `eprintfn $"{err.Message}"`, so that render
/// is a direct path from a hostile child to an operator's terminal and log. These tests pin the two
/// guarantees that make it safe: every embedded fragment is *sanitized* (terminal and bidirectional
/// controls, `CR`/`LF`, and the Unicode line/paragraph separators cannot survive it) and *bounded*
/// (100 KB of stderr renders as the same small preview as 100 bytes), while the structured fields keep
/// the caller's bytes untouched.
[<TestFixture>]
type ProcessErrorSanitizationTests() =

    // Named rather than spelled as escapes so the payloads below read as the attack they are.
    static let esc = char 0x1B // ANSI introducer: cursor moves, screen erase, colour
    static let bel = char 0x07 // terminal bell
    static let nul = char 0x00
    static let lineSeparator = char 0x2028 // LS - not a control character, still breaks a line
    static let paragraphSeparator = char 0x2029 // PS
    static let rightToLeftOverride = char 0x202E // the Trojan Source (CVE-2021-42574) workhorse
    static let popDirectionalFormatting = char 0x202C
    static let firstStrongIsolate = char 0x2068
    static let popDirectionalIsolate = char 0x2069
    static let leftToRightMark = char 0x200E
    static let rightToLeftMark = char 0x200F
    static let arabicLetterMark = char 0x061C

    /// One fragment carrying every injection this render has to neutralize: an ANSI erase-screen plus
    /// colour sequence, `BEL`, `NUL`, a `CR` and an `LF` (forged extra log lines), the form feed and
    /// `NEL` that also move a terminal to a new line, the Unicode line/paragraph separators a viewer
    /// renders as newlines too, and the bidi-formatting controls that visually reorder the text around
    /// them.
    static let hostile =
        String.concat
            ""
            [ $"{esc}[2J{esc}[1;31mERASED"
              $"{bel}{nul}"
              "carriage\rreturn\nnewline"
              $"{char 0x0C}formfeed{char 0x85}nel"
              $"{lineSeparator}LS{paragraphSeparator}PS"
              $"{rightToLeftOverride}drowssap{popDirectionalFormatting}"
              $"{firstStrongIsolate}isolate{popDirectionalIsolate}"
              $"{leftToRightMark}{rightToLeftMark}{arabicLetterMark}" ]

    /// Deliberately an independent restatement of the rule rather than a call into the library's own
    /// internal predicate: a test that asked the implementation what "unsafe" means could not catch the
    /// implementation redefining it.
    static let isDisplayUnsafe (c: char) =
        (Char.IsControl c && c <> '\t')
        || c = lineSeparator
        || c = paragraphSeparator
        || c = arabicLetterMark
        || c = leftToRightMark
        || c = rightToLeftMark
        || (c >= char 0x202A && c <= char 0x202E)
        || (c >= char 0x2066 && c <= char 0x2069)

    static let assertDisplaySafe (context: string) (rendered: string) =
        let offending = rendered |> Seq.tryFindIndex isDisplayUnsafe

        match offending with
        | Some index ->
            let codePoint = (int rendered[index]).ToString "X4"

            let detail: string =
                $"{context}: U+{codePoint} reached the rendered message at index {index}"

            Assert.Fail detail
        | None -> ()

    /// Every case of the union, each fed the hostile fragment in every string field it carries, so no
    /// producer of any case can route text into the render along an unsanitized path. Both branches of
    /// the cases that have one (`NotFound`, `Signalled`, `JsonRpc`, `OutputTooLarge`) are listed.
    static let everyCase: (string * ProcessError) list =
        [ "Spawn", ProcessError.Spawn(hostile, hostile)
          "NotFound", ProcessError.NotFound(hostile, Some hostile)
          "NotFound/unsearched", ProcessError.NotFound(hostile, None)
          "Exit", ProcessError.Exit(hostile, 1, hostile, hostile)
          "Signalled", ProcessError.Signalled(hostile, Some 9, hostile, hostile)
          "Signalled/unknown", ProcessError.Signalled(hostile, None, hostile, hostile)
          "Timeout", ProcessError.Timeout(hostile, TimeSpan.FromSeconds 3.0, hostile, hostile)
          "Unobserved", ProcessError.Unobserved(hostile, hostile)
          "Cancelled", ProcessError.Cancelled hostile
          "NotReady", ProcessError.NotReady(hostile, TimeSpan.FromSeconds 1.0)
          "Parse", ProcessError.Parse(hostile, hostile)
          "RetryPredicate",
          ProcessError.RetryPredicate(hostile, ProcessError.Exit(hostile, 3, hostile, hostile), hostile)
          "JsonRpc", ProcessError.JsonRpc(hostile, hostile, -32601, hostile, Some hostile)
          "JsonRpc/no detail", ProcessError.JsonRpc(hostile, hostile, -32602, "", None)
          "OutputTooLarge/lines", ProcessError.OutputTooLarge(hostile, Some 10, None, 11, 512)
          "OutputTooLarge/bytes", ProcessError.OutputTooLarge(hostile, None, Some 10, 0, 512)
          "OutputTooLarge/events", ProcessError.OutputTooLarge(hostile, None, None, 3, 512)
          "OutputTooLarge/other", ProcessError.OutputTooLarge(hostile, None, None, 0, 512)
          "Stdin", ProcessError.Stdin(hostile, hostile)
          "ResourceLimit", ProcessError.ResourceLimit hostile
          "Adopt", ProcessError.Adopt(4321, hostile)
          "CassetteMiss", ProcessError.CassetteMiss hostile
          "Io", ProcessError.Io hostile
          "Unsupported", ProcessError.Unsupported hostile ]

    /// The exception a consumer actually sees, obtained the way consumers get it (`GetValueOrThrow`)
    /// rather than through the internal constructor.
    static let processExceptionFor (error: ProcessError) : ProcessException =
        let failed: Result<int, ProcessError> = Error error

        let thrown =
            try
                ResultExtensions.GetValueOrThrow failed |> ignore
                None
            with :? ProcessException as ex ->
                // Not a swallow: this IS the value under test - `GetValueOrThrow` reports an `Error`
                // exactly this way, and any other exception type is a real defect and stays unhandled.
                Some ex

        match thrown with
        | Some ex -> ex
        | None -> failwith "GetValueOrThrow must throw a ProcessException for an Error result"

    static let hugeStream = String('x', 100 * 1024)

    [<Test>]
    member _.``every error case renders one display-safe line from hostile fragments``() =
        for name, error in everyCase do
            assertDisplaySafe $"{name}.Message" error.Message
            assertDisplaySafe $"{name}.ToString()" (error.ToString())
            assertDisplaySafe $"{name} ProcessException.Message" (processExceptionFor error).Message

            let mirrors: string = $"{name}: ToString() must stay the Message"
            Assert.That(error.ToString(), Is.EqualTo error.Message, mirrors)

            let carried: string =
                $"{name}: ProcessException must carry the same rendered message"

            Assert.That((processExceptionFor error).Message, Is.EqualTo error.Message, carried)

    [<Test>]
    member _.``a hostile fragment is neutralized, not dropped``() =
        // The replacement character is what makes the neutralization visible: an operator sees that
        // something was removed instead of reading a silently shortened, plausible-looking line.
        let rendered = (ProcessError.Parse("tool", hostile)).Message
        let marked: string = "each unsafe character must render as U+FFFD"
        Assert.That(rendered, Does.Contain(string (char 0xFFFD)), marked)

        let kept: string = "the printable text around the injection must survive"
        Assert.That(rendered, Does.Contain "ERASED", kept)
        Assert.That(rendered, Does.Contain "isolate", kept)
        Assert.That(rendered, Does.StartWith "failed to parse output of 'tool': ")

    [<Test>]
    member _.``printable Unicode, TAB, and astral characters survive unchanged``() =
        // A TAB is a legitimate column separator in tool output and must not be mangled; the fish is a
        // surrogate pair, which the cap must treat as one character and never split.
        let printable = "üñî çø∂é\tcolumn\tsplit 中文 🐟 — done"

        let parse = ProcessError.Parse(printable, printable)
        let expected: string = $"failed to parse output of '{printable}': {printable}"
        Assert.That(parse.Message, Is.EqualTo expected)

        let exited = ProcessError.Exit(printable, 2, "", printable)
        Assert.That(exited.Message, Is.EqualTo $"'{printable}' exited with code 2: {printable}")

    [<Test>]
    member _.``a stream-carrying failure quotes only the last non-blank stderr line``() =
        // The actionable line is the last one (`git push` ENDS with `remote: permission denied`), and
        // the render must never grow back into the multi-line dump it used to be.
        let stderr = "warning: first\nwarning: second\r\nremote: permission denied\n   \n\n"
        let stdout = "unrelated stdout chatter"

        let exited = ProcessError.Exit("git", 1, stdout, stderr)
        Assert.That(exited.Message, Is.EqualTo "'git' exited with code 1: remote: permission denied")

        let signalled = ProcessError.Signalled("git", Some 9, stdout, stderr)
        Assert.That(signalled.Message, Is.EqualTo "'git' was terminated by signal 9: remote: permission denied")

        let killed = ProcessError.Signalled("git", None, stdout, stderr)
        Assert.That(killed.Message, Is.EqualTo "'git' was killed: remote: permission denied")

        let timedOut = ProcessError.Timeout("git", TimeSpan.FromSeconds 5.0, stdout, stderr)

        Assert.That(timedOut.Message, Is.EqualTo "'git' timed out after 5s: remote: permission denied")

        let dropped: string = "the earlier stderr lines must not reach the one-line render"
        Assert.That(exited.Message, Does.Not.Contain "warning: first", dropped)

        let stdoutStaysOut: string = "stdout is on the accessor, not in the message"
        Assert.That(exited.Message, Does.Not.Contain stdout, stdoutStaysOut)

    [<Test>]
    member _.``the tail rule ends a line on every terminator, not just LF``() =
        // A child that ends its lines with U+2028 (or a form feed, or NEL) rather than LF must not
        // smuggle the earlier text into the quoted tail: the render splits on the same terminators
        // .NET's own `String.ReplaceLineEndings` recognizes, and every one of them is neutralized if it
        // does survive inside a line.
        let separators =
            [ "LS", lineSeparator
              "PS", paragraphSeparator
              "FF", char 0x0C
              "NEL", char 0x85 ]

        for name, separator in separators do
            let stderr = $"noise the operator must not see{separator}real: the last line"
            let exited = ProcessError.Exit("tool", 1, "", stderr)

            let quoted: string = $"{name} must end the line the tail is taken from"
            Assert.That(exited.Message, Is.EqualTo "'tool' exited with code 1: real: the last line", quoted)

    [<Test>]
    member _.``a blank stream adds no dangling separator``() =
        let blank = ProcessError.Exit("git", 2, "", "   \n\n\t\n")
        Assert.That(blank.Message, Is.EqualTo "'git' exited with code 2")

        let empty = ProcessError.Timeout("git", TimeSpan.FromSeconds 1.0, "", "")
        Assert.That(empty.Message, Is.EqualTo "'git' timed out after 1s")

    [<Test>]
    member _.``a 100 KB stream renders as a small stable preview with an ellipsis``() =
        let exited = ProcessError.Exit("tool", 1, hugeStream, hugeStream)

        let bounded: string = "a 100 KB stderr must not reach the log line"
        Assert.That(exited.Message.Length, Is.LessThan 600, bounded)
        Assert.That(exited.Message, Does.EndWith "…")

        // Stable: ten times the input renders the same message, so the log line's size is a property of
        // the render, not of whatever the child decided to write.
        let tenfold =
            ProcessError.Exit("tool", 1, hugeStream, String.replicate 10 hugeStream)

        Assert.That(tenfold.Message, Is.EqualTo exited.Message)

    [<Test>]
    member _.``an oversized detail is bounded on every case that carries one``() =
        let cases: (string * ProcessError) list =
            [ "Spawn", ProcessError.Spawn("tool", hugeStream)
              "Unobserved", ProcessError.Unobserved("tool", hugeStream)
              "Parse", ProcessError.Parse("tool", hugeStream)
              "JsonRpc", ProcessError.JsonRpc("peer", "initialize", -32603, hugeStream, Some hugeStream)
              "Stdin", ProcessError.Stdin("tool", hugeStream)
              "ResourceLimit", ProcessError.ResourceLimit hugeStream
              "Adopt", ProcessError.Adopt(9, hugeStream)
              "Io", ProcessError.Io hugeStream
              "Unsupported", ProcessError.Unsupported hugeStream
              "Cancelled/program name", ProcessError.Cancelled hugeStream ]
        // `NotFound`'s searched path is deliberately absent: it is not embedded and bounded, it is
        // reported as a count - see the two tests below.

        for name, error in cases do
            let bounded: string = $"{name}: a 100 KB fragment must render bounded"
            Assert.That(error.Message.Length, Is.LessThan 600, bounded)

            let marked: string = $"{name}: a truncated fragment must be marked with an ellipsis"
            Assert.That(error.Message, Does.Contain "…", marked)

    [<Test>]
    member _.``a not-found failure counts the searched PATH instead of quoting it``() =
        // `Searched` is a whole `PATH` environment value - thousands of characters on an ordinary
        // machine. Neither quoting it nor quoting an arbitrary 512-character slice of it belongs in a
        // log line, so the message reports how many entries were probed and the value stays on the
        // field. This pins the wording, since the count is now the only thing the line says about it.
        let separator = string IO.Path.PathSeparator

        let path =
            String.concat separator [ "/opt/tools/bin"; "SENTINEL-DIR"; "/usr/local/bin" ]

        let counted = ProcessError.NotFound("tool", Some path)
        Assert.That(counted.Message, Is.EqualTo "program 'tool' was not found (searched 3 PATH entries)")

        let excluded: string = "the PATH value itself must not reach the message"
        Assert.That(counted.Message, Does.Not.Contain "SENTINEL", excluded)

        let single = ProcessError.NotFound("tool", Some "/usr/local/bin")
        Assert.That(single.Message, Is.EqualTo "program 'tool' was not found (searched 1 PATH entry)")

        // Positional empty entries in a non-empty PATH are the effective working directory on POSIX,
        // while Windows drops them. The message follows the resolver's platform rule.
        let padded =
            ProcessError.NotFound("tool", Some $"{separator}{separator}/opt/bin{separator}")

        let paddedDescription =
            if OperatingSystem.IsWindows() then
                "1 PATH entry"
            else
                "4 PATH entries"

        Assert.That(padded.Message, Is.EqualTo $"program 'tool' was not found (searched {paddedDescription})")

        // A separator-only value has two positional empty entries on POSIX, but none on Windows.
        let separatorOnly = ProcessError.NotFound("tool", Some separator)

        let separatorOnlyDescription =
            if OperatingSystem.IsWindows() then
                "an empty PATH"
            else
                "2 PATH entries"

        Assert.That(
            separatorOnly.Message,
            Is.EqualTo $"program 'tool' was not found (searched {separatorOnlyDescription})"
        )

        // No PATH search applied (a path-form program): no parenthetical at all.
        let unsearched = ProcessError.NotFound("./tool", None)
        Assert.That(unsearched.Message, Is.EqualTo "program './tool' was not found")

    [<Test>]
    member _.``a huge or hostile searched PATH neither floods nor injects``() =
        let separator = string IO.Path.PathSeparator

        let huge =
            String.concat separator (List.replicate 4000 "/some/rather/long/directory/name")

        let flooded = ProcessError.NotFound("tool", Some huge)

        let bounded: string =
            "a PATH of 4000 entries (over 100 KB of text) must render as short a line as a one-entry PATH"

        Assert.That(flooded.Message, Is.EqualTo "program 'tool' was not found (searched 4000 PATH entries)", bounded)

        let whole: string = "the searched path stays whole on the field"

        match flooded with
        | ProcessError.NotFound(_, searched) -> Assert.That(searched, Is.EqualTo(Some huge), whole)
        | other -> Assert.Fail $"expected a NotFound, got {other}"

        // A caller-controlled PATH (a `Command.Env` override can set one) carrying the full injection
        // payload: a count is a number, so nothing of it can reach the terminal either way.
        let hostilePath = String.concat separator [ hostile; hostile ]
        assertDisplaySafe "NotFound/searched" (ProcessError.NotFound(hostile, Some hostilePath)).Message

    [<Test>]
    member _.``nesting a RetryPredicate cannot walk around the bound``() =
        let leaf = ProcessError.Exit("tool", 1, hugeStream, hugeStream)

        let nest depth =
            List.fold (fun inner _ -> ProcessError.RetryPredicate("tool", inner, hugeStream)) leaf [ 1..depth ]

        let shallow = (nest 1).Message
        let deep = (nest 12).Message

        let bounded: string = "a nested original must be previewed as one bounded fragment"
        Assert.That(deep.Length, Is.LessThan 1800, bounded)

        let stable: string = "twelve levels of nesting must render exactly as one level"
        Assert.That(deep.Length, Is.EqualTo shallow.Length, stable)

        assertDisplaySafe "nested RetryPredicate" (nest 12).Message

    [<Test>]
    member _.``RetryPredicate with a missing Original renders safely and exposes no recursive values``() =
        let missingOriginal = Unchecked.defaultof<ProcessError>
        let error = ProcessError.RetryPredicate(hostile, missingOriginal, hugeStream)
        let message = error.Message

        Assert.That(message, Does.EndWith "original attempt: unavailable")
        Assert.That(message.Length, Is.LessThan 1200, "the fallback render must stay bounded")
        Assert.That(error.ToString(), Is.EqualTo message)
        assertDisplaySafe "RetryPredicate/missing Original" message

        Assert.That(error.Stdout, Is.EqualTo(None: string option))
        Assert.That(error.StdoutBytes, Is.EqualTo(None: byte[] option))
        Assert.That(error.Stderr, Is.EqualTo(None: string option))
        Assert.That(error.Combined, Is.EqualTo(None: string option))
        Assert.That(error.Code, Is.EqualTo(None: int option))
        Assert.That(error.Signal, Is.EqualTo(None: int option))

        match error with
        | ProcessError.RetryPredicate(_, original, _) ->
            Assert.That(obj.ReferenceEquals(original, null), Is.True, "the public case shape remains unchanged")
        | other -> Assert.Fail $"expected a RetryPredicate, got {other}"

    [<Test>]
    member _.``the structured fields keep the caller's bytes untouched``() =
        let original = ProcessError.Exit(hostile, 42, hugeStream, hostile)

        match original with
        | ProcessError.Exit(program, code, stdout, stderr) ->
            Assert.That(program, Is.EqualTo hostile)
            Assert.That(code, Is.EqualTo 42)
            Assert.That(stdout, Is.EqualTo hugeStream)
            Assert.That(stderr, Is.EqualTo hostile)
        | other -> Assert.Fail $"expected an Exit, got {other}"

        Assert.That(original.Program, Is.EqualTo(Some hostile))
        Assert.That(original.Stdout, Is.EqualTo(Some hugeStream))
        Assert.That(original.Stderr, Is.EqualTo(Some hostile))
        Assert.That(original.Combined, Is.EqualTo(Some(hugeStream + "\n" + hostile)))
        Assert.That(original.Code, Is.EqualTo(Some 42))
        Assert.That(original.StdoutBytes, Is.EqualTo(None: byte[] option))

        // The same through a `RetryPredicate`'s delegating accessors.
        let wrapped = ProcessError.RetryPredicate("tool", original, hostile)
        Assert.That(wrapped.Stdout, Is.EqualTo(Some hugeStream))
        Assert.That(wrapped.Stderr, Is.EqualTo(Some hostile))
        Assert.That(wrapped.Combined, Is.EqualTo(Some(hugeStream + "\n" + hostile)))
        Assert.That(wrapped.Code, Is.EqualTo(Some 42))
        Assert.That(wrapped.Signal, Is.EqualTo(None: int option))
        Assert.That(wrapped.StdoutBytes, Is.EqualTo(None: byte[] option))
        Assert.That(wrapped.Message, Does.Contain "exited with code 42")

        let wrappedSignal =
            ProcessError.RetryPredicate("tool", ProcessError.Signalled("tool", Some 15, "out", "err"), "detail")

        Assert.That(wrappedSignal.Code, Is.EqualTo(None: int option))
        Assert.That(wrappedSignal.Signal, Is.EqualTo(Some 15))

        match wrapped with
        | ProcessError.RetryPredicate(_, inner, detail) ->
            Assert.That(detail, Is.EqualTo hostile)
            Assert.That(inner, Is.EqualTo original)
        | other -> Assert.Fail $"expected a RetryPredicate, got {other}"

    [<Test>]
    member _.``StdoutBytes carries exact non-UTF-8 bytes while Stdout/Message stay the safe decoded preview``() =
        // Invalid UTF-8: a lone continuation byte (0x80) and a truncated lead byte (0xC3 with nothing to
        // continue it) — .NET's UTF-8 decoder replaces both with U+FFFD when decoding to text, so the
        // decoded `Stdout`/`Message` are lossy by construction; the point of `StdoutBytes` is that the
        // ORIGINAL bytes still round-trip exactly alongside that lossy text.
        let rawBytes = [| 0x68uy; 0x69uy; 0x80uy; 0xC3uy; 0x41uy |]
        let decodedText = Text.Encoding.UTF8.GetString rawBytes

        // `ProcessResult.FailureError` is the ONE place `StdoutBytes` is ever attached (via
        // `ProcessError.AttachStdoutBytes`'s identity-keyed side channel, not a constructor field — see
        // its doc), so a bytes-based checking verb is exercised through `ProcessResult<byte[]>` here
        // rather than by constructing the `Exit` case directly.
        let error =
            match
                ProcessResult.Create rawBytes "" (Outcome.Exited 1) TimeSpan.Zero
                |> ProcessResult.ensureSuccess
            with
            | Error e -> e
            | Ok _ -> failwith "expected exit 1 to fail ensureSuccess"

        let roundTrip: string =
            "the exact bytes must round-trip through StdoutBytes, unchanged"

        Assert.That(error.StdoutBytes, Is.EqualTo(Some rawBytes), roundTrip)

        let lossyText: string =
            "Stdout stays the already-decoded text, never reconstructed from bytes"

        Assert.That(error.Stdout, Is.EqualTo(Some decodedText), lossyText)

        // The decoded replacement characters are themselves display-safe (U+FFFD is not a control
        // character), so the rendered Message never throws or misbehaves on the invalid input.
        assertDisplaySafe "Exit.Message (non-UTF-8 stdout)" error.Message

        // A text-based checking verb never fabricates bytes from an already-decoded string.
        let textOnly =
            match
                ProcessResult.Create decodedText "" (Outcome.Exited 1) TimeSpan.Zero
                |> ProcessResult.ensureSuccess
            with
            | Error e -> e
            | Ok _ -> failwith "expected exit 1 to fail ensureSuccess"

        Assert.That(textOnly.StdoutBytes, Is.EqualTo(None: byte[] option), "text-based capture must stay None")

        // RetryPredicate forwards StdoutBytes through the same delegation as Stdout/Stderr/Code.
        let wrapped = ProcessError.RetryPredicate("tool", error, "predicate detail")
        Assert.That(wrapped.StdoutBytes, Is.EqualTo(Some rawBytes), "RetryPredicate must forward StdoutBytes")

    [<Test>]
    member _.``JSON-RPC data stays whole on the field and out of the message``() =
        let secret = "SENTINEL-" + hugeStream

        let error =
            ProcessError.JsonRpc("peer", "initialize", -32602, "invalid params", Some secret)

        match error with
        | ProcessError.JsonRpc(_, _, _, _, data) -> Assert.That(data, Is.EqualTo(Some secret))
        | other -> Assert.Fail $"expected a JsonRpc, got {other}"

        let excluded: string =
            "the peer's raw data payload has never been part of the message"

        Assert.That(error.Message, Does.Not.Contain "SENTINEL", excluded)
        Assert.That(error.Message, Is.EqualTo "'peer' answered 'initialize' with JSON-RPC error -32602: invalid params")
