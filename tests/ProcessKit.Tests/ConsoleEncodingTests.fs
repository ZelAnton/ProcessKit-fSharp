namespace ProcessKit.Tests

open System
open System.IO
open System.Runtime.InteropServices
open System.Text
open System.Threading.Tasks
open NUnit.Framework
open ProcessKit

/// Independent bindings for the two Win32 entry points the library resolves the console code page
/// through, so these tests check `ConsoleEncoding` against what the OS itself reports rather than
/// against a second copy of the library's own answer. Windows-only: declared here, called solely under
/// `isWindows`.
module private NativeConsoleCodePage =

    [<DllImport("kernel32.dll")>]
    extern uint32 GetConsoleOutputCP()

    [<DllImport("kernel32.dll")>]
    extern uint32 GetOEMCP()

/// `ConsoleEncoding.current` / `Command.ConsoleEncoding()` (T-229) — the opt-in one-liner for reading a
/// legacy Windows console program whose non-ASCII output is written in a code page rather than UTF-8.
///
/// Three things have to hold at once for the helper to be worth having: the UTF-8 default must be
/// completely untouched for everyone who does not ask for this, the resolved code page must be the one
/// the OS actually reports (not a guess), and off Windows the whole thing must be a genuine no-op —
/// the same UTF-8 instance a plain `Command` already decodes with, no platform call at all.
[<TestFixture>]
type ConsoleEncodingTests() =

    let isWindows = RuntimeInformation.IsOSPlatform OSPlatform.Windows

    /// The console output code page Windows reports for THIS process, resolved the way the documented
    /// contract states: the console's own output code page, or the system OEM code page when the process
    /// has no console at all (`GetConsoleOutputCP` returns its documented 0 sentinel).
    let codePageWindowsReports () =
        match NativeConsoleCodePage.GetConsoleOutputCP() with
        | 0u -> int (NativeConsoleCodePage.GetOEMCP())
        | codePage -> int codePage

    /// A probe text that (a) survives a round trip through `encoding`, and (b) encodes to bytes that a
    /// UTF-8 decode does NOT reproduce — so a test using it can tell the two decodings apart instead of
    /// passing on ASCII that every encoding agrees on.
    let probeText (encoding: Encoding) =
        [ "Grüße"; "Привет"; "señor"; "±°²" ]
        |> List.tryFind (fun text ->
            let bytes = encoding.GetBytes text
            encoding.GetString bytes = text && Encoding.UTF8.GetString bytes <> text)

    [<Test>]
    member _.``the captured-output default is UTF-8, unchanged by the helper existing``() =
        // The whole point of the helper being opt-in: a command nobody asked to decode differently is
        // byte-for-byte the same command it was before this feature existed.
        let config = (Command.create "tool").Config

        Assert.That(config.StdoutEncoding, Is.SameAs Encoding.UTF8, "stdout must still default to UTF-8")
        Assert.That(config.StderrEncoding, Is.SameAs Encoding.UTF8, "stderr must still default to UTF-8")

    [<Test>]
    member _.``ConsoleEncoding.current resolves the code page Windows itself reports``() =
        if not isWindows then
            Assert.Ignore "Windows-only: there is no second, legacy console code page on Unix"
        else
            let expected = codePageWindowsReports ()

            Assert.That(
                ConsoleEncoding.current().CodePage,
                Is.EqualTo expected,
                "the resolved encoding must be the console output / OEM code page the OS reports"
            )

    [<Test>]
    member _.``ConsoleEncoding.current is the unchanged UTF-8 default off Windows``() =
        if isWindows then
            Assert.Ignore "POSIX-only: Windows resolves a real console code page instead"
        else
            // Not merely "a UTF-8 encoding" but the very instance `Command` already decodes with, so the
            // no-op claim is an identity rather than an approximation.
            Assert.That(
                ConsoleEncoding.current (),
                Is.SameAs Encoding.UTF8,
                "off Windows the helper must resolve to the same UTF-8 instance the default uses"
            )

    [<Test>]
    member _.``ConsoleEncoding() applies the resolved encoding to both captured streams``() =
        let resolved = ConsoleEncoding.current ()
        let config = (Command.create "tool").ConsoleEncoding().Config

        Assert.That(
            config.StdoutEncoding.CodePage,
            Is.EqualTo resolved.CodePage,
            "stdout must decode with the resolved console encoding"
        )

        Assert.That(
            config.StderrEncoding.CodePage,
            Is.EqualTo resolved.CodePage,
            "stderr must decode with the resolved console encoding"
        )

    [<Test>]
    member _.``a later explicit Encoding still wins over the helper, and the other way round``() =
        // An ordinary builder knob, not a mode: last call in the chain wins, in either order.
        let helperThenExplicit =
            (Command.create "tool").ConsoleEncoding().Encoding Encoding.Latin1

        let explicitThenHelper =
            ((Command.create "tool").Encoding Encoding.Latin1).ConsoleEncoding()

        Assert.That(
            helperThenExplicit.Config.StdoutEncoding.CodePage,
            Is.EqualTo Encoding.Latin1.CodePage,
            "an explicit Encoding after the helper must win"
        )

        Assert.That(
            explicitThenHelper.Config.StdoutEncoding.CodePage,
            Is.EqualTo(ConsoleEncoding.current().CodePage),
            "the helper after an explicit Encoding must win"
        )

    [<Test>]
    member _.``repeated resolution is stable and never fails``() =
        // The `CodePagesEncodingProvider` registration behind this is process-wide and additive, so it
        // is done exactly once behind a `lazy`; repeated calls must be plain reads that neither throw
        // nor drift to a different answer.
        let first = ConsoleEncoding.current ()

        for _ in 1..5 do
            let again = ConsoleEncoding.current ()

            Assert.That(again.CodePage, Is.EqualTo first.CodePage, "every call must resolve the same code page")

    [<Test>]
    member _.``a child's code-page bytes decode correctly with the helper and mangle without it``() : Task =
        task {
            if not isWindows then
                Assert.Ignore "Windows-only: the code-page-vs-UTF-8 divergence this proves is a Windows one"
            else
                let encoding = ConsoleEncoding.current ()

                match probeText encoding with
                | None ->
                    // A UTF-8 console (`chcp 65001`) or an exotic code page with no non-ASCII probe text:
                    // there is no observable difference between the two decodings to assert on.
                    Assert.Ignore $"console code page {encoding.CodePage} has no probe text that differs from UTF-8"
                | Some text ->
                    // The child copies bytes we control verbatim (`type` does not transcode), so this
                    // exercises the decode path end to end without depending on what any particular
                    // legacy tool happens to emit.
                    let path =
                        Path.Combine(
                            Path.GetTempPath(),
                            "processkit-console-encoding-" + Guid.NewGuid().ToString "N" + ".txt"
                        )

                    File.WriteAllBytes(path, encoding.GetBytes text)

                    try
                        let command =
                            Command.create "cmd.exe"
                            |> Command.args [ "/c"; "type"; path ]
                            |> Command.timeout (TimeSpan.FromSeconds 30.0)

                        match! command.ConsoleEncoding().OutputStringAsync() with
                        | Error error -> Assert.Fail $"the ConsoleEncoding run failed: {error.Message}"
                        | Ok result ->
                            Assert.That(
                                result.Stdout,
                                Is.EqualTo text,
                                "ConsoleEncoding() must decode the child's code-page output back to the original text"
                            )

                        match! command.OutputStringAsync() with
                        | Error error -> Assert.Fail $"the default-encoding run failed: {error.Message}"
                        | Ok result ->
                            Assert.That(
                                result.Stdout,
                                Is.Not.EqualTo text,
                                "the UTF-8 default must still mangle code-page bytes - that is the problem the helper solves"
                            )
                    finally
                        try
                            File.Delete path
                        with :? IOException ->
                            // A temp file the OS still holds open is not this test's failure; it is
                            // collected with the rest of TEMP.
                            ()
        }

    [<Test>]
    member _.``the helper leaves an ordinary UTF-8 child's output untouched off Windows``() : Task =
        task {
            if isWindows then
                Assert.Ignore "POSIX-only: proves the no-op path against a real spawn"
            else
                let text = "Grüße Привет"

                let command =
                    Command.create "printf"
                    |> Command.args [ text ]
                    |> Command.timeout (TimeSpan.FromSeconds 30.0)

                match! command.ConsoleEncoding().OutputStringAsync() with
                | Error error -> Assert.Fail $"the ConsoleEncoding run failed: {error.Message}"
                | Ok result ->
                    Assert.That(
                        result.Stdout,
                        Is.EqualTo text,
                        "off Windows the helper must decode exactly as the UTF-8 default does"
                    )
        }
