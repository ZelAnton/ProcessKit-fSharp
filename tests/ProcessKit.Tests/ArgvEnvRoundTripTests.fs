namespace ProcessKit.Tests

open System
open System.IO
open System.Text
open System.Text.Json
open NUnit.Framework
open FsCheck
open FsCheck.FSharp
open ProcessKit

/// Property-based round-trip tests for argument-vector (`Command.Arg`/`Command.Args`) and
/// environment (`Command.Env`) marshalling: they assert that whatever a caller hands the builder
/// arrives at a **real spawned child** byte-for-byte, element by element — exercising the manual,
/// platform-divergent native layer end to end (`Native.Windows.buildWindowsCommandLine`/
/// `quoteWindowsArg`, the `CommandLineToArgvW` quoting rules, on Windows; the argv/envp block
/// marshalling in `Native.Posix` on POSIX). The existing FsCheck set (`PumpPropertyTests`) only
/// covers output framing; adversarial argument quoting — historically the single most error-prone
/// corner of a process library — was only spot-checked. The generators below deliberately hammer the
/// hard shapes: nested/embedded quotes, runs of backslashes, a backslash immediately before a quote,
/// trailing backslashes, empty arguments, spaces/tabs/newlines, Unicode (including non-BMP
/// surrogate-pair code points), and the `cmd.exe`/batch-significant `% ^ &`.
///
/// **Echo-child mechanism (no existing test-helper pattern fits, so it is documented here per the
/// task's requirement).** The sibling spawn tests (`PosixSpawnCleanupTests`, `WhichResolutionTests`)
/// launch `cmd.exe`/`/bin/sh`, but a shell child is unusable here: `cmd.exe` re-parses its command
/// line with its OWN rules (`% ^ &` expansion, caret escaping, ...), which would corrupt exactly the
/// characters under test, and `sh` positional-parameter echoing differs across `dash`/`bash`/BusyBox.
/// What we need is a child that reports its **raw** argv — the single `CommandLineToArgvW` parse
/// (Windows) / `execve` argv (POSIX) our marshalling targets — with no shell in between. A .NET
/// child's `args` (its parsed entry-point argv) and `Environment.GetEnvironmentVariable` give exactly
/// that, identically on every platform. So a tiny console app that emits its argv / looked-up env
/// values as a UTF-8 JSON array is generated into a temp directory and built ONCE
/// (`[<OneTimeSetUp>]`), then run per sample via `dotnet exec <dll>` — which loads the dll into the
/// host process and hands it the argv directly (a single parse, no re-quoting), unlike `dotnet run` or
/// a shell wrapper (each of which would re-marshal). That keeps the steady-state cost to ~0.1s/spawn
/// (a plain runtime start, no per-sample compile), so the FsCheck sample count is deliberately modest
/// (`iterations`) to keep an ordinary `dotnet test` fast; the deterministic example tests pin the
/// exact adversarial shapes so the property's job is breadth, not guaranteed coverage. The helper is a
/// throwaway temp project (deleted in `[<OneTimeTearDown>]`), not a permanent `tests/` helper project.
/// It targets `net10.0`: the pinned 10.0.x SDK builds it with its in-box reference pack (no cross-TFM
/// restore), and the net10.0 runtime is present in every CI leg (the `test` matrix installs both
/// 8.0.x and 10.0.x; the alpine/cgroup legs are net10-only) and locally, so `dotnet exec` always
/// resolves it (`RollForward=LatestMajor` is belt-and-suspenders).
///
/// Sequential (no `[<Parallelizable>]`): each `[<Test>]` drives many child spawns; there is no shared
/// mutable state to protect — this simply keeps all of them from piling on at once.
[<TestFixture>]
type ArgvEnvRoundTripTests() =

    // Modest FsCheck sample count per property: each sample spawns a real process (~0.1s), so the
    // default 100 would dominate `dotnet test`. The deterministic example tests below pin the exact
    // adversarial shapes regardless, so the property covers breadth, not guaranteed coverage.
    static let iterations = 30

    // Adversarial character pool for generated arguments/values. Includes the Windows-quoting-critical
    // characters (`"`, `\`), whitespace the quoter splits on (space, tab, newline, vertical tab as the
    // decimal escape `\011`), path characters (`/`, `:`), the `cmd.exe`/batch-significant `% ^ &`, an
    // `=` (a legal argv/env-value character), plus a handful of ordinary ASCII and BMP Unicode. No NUL:
    // the builder rejects an embedded `'\000'`, and both POSIX and Windows marshalling truncate at one.
    static let specialChars =
        [ '"'
          '\\'
          '/'
          ' '
          '\t'
          '\n'
          '\011'
          '%'
          '^'
          '&'
          '='
          ';'
          '\''
          ':'
          'a'
          'M'
          '7'
          'é'
          'ü'
          'ñ'
          '中'
          '€' ]

    // Non-BMP code points — each a surrogate PAIR in UTF-16, so they exercise the argv/env path with
    // characters that are two `char`s wide (emoji, mathematical digits, CJK-extension ideographs).
    static let nonBmpCodePoints = [ 0x1F600; 0x1F680; 0x1D7D8; 0x10437; 0x2F81A; 0x24B62 ]

    static let poolStringGen: Gen<string> =
        Gen.listOf (Gen.elements specialChars)
        |> Gen.map (fun cs -> String(List.toArray cs))

    // A run of 1..5 backslashes, ending the argument, immediately before a quote, or before an ordinary
    // character — the three cases `quoteWindowsArg` doubles-or-not differently.
    static let backslashRunGen: Gen<string> =
        gen {
            let! prefix = poolStringGen
            let! n = Gen.choose (1, 5)
            let! trailing = Gen.elements [ ""; "\""; "x" ]
            return prefix + String('\\', n) + trailing
        }

    static let quoteRunGen: Gen<string> =
        Gen.choose (1, 4) |> Gen.map (fun n -> String('"', n))

    static let unicodeSpliceGen: Gen<string> =
        gen {
            let! before = poolStringGen
            let! cp = Gen.elements nonBmpCodePoints
            let! after = poolStringGen
            return before + Char.ConvertFromUtf32 cp + after
        }

    // A single generated argument (empty allowed — a legitimate adversarial argv element).
    static let argGen: Gen<string> =
        Gen.frequency
            [ 3, poolStringGen
              2, backslashRunGen
              2, unicodeSpliceGen
              1, quoteRunGen
              1, Gen.constant ""
              1, Gen.constant "\\"
              1, Gen.constant "a\\\"b" ]

    // A single generated environment VALUE — the same adversarial shapes but guaranteed non-empty:
    // Windows treats an empty environment value as "unset", so an empty value has no meaningful
    // round-trip and is excluded here (the argv side still covers the empty case).
    static let nonEmptyPoolGen: Gen<string> =
        Gen.nonEmptyListOf (Gen.elements specialChars)
        |> Gen.map (fun cs -> String(List.toArray cs))

    static let envValueGen: Gen<string> =
        Gen.frequency
            [ 3, nonEmptyPoolGen
              2, backslashRunGen
              2, unicodeSpliceGen
              1, quoteRunGen
              1, Gen.constant "=leading=equals"
              1, Gen.constant "trailing space and\ttab " ]

    static let argVectorGen: Gen<string list> =
        gen {
            let! n = Gen.choose (0, 6)
            return! Gen.listOfLength n argGen
        }

    static let envValuesGen: Gen<string list> =
        gen {
            let! n = Gen.choose (0, 6)
            return! Gen.listOfLength n envValueGen
        }

    static let helperCsproj =
        """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <RollForward>LatestMajor</RollForward>
    <EnableNETAnalyzers>false</EnableNETAnalyzers>
    <GenerateDocumentationFile>false</GenerateDocumentationFile>
  </PropertyGroup>
</Project>
"""

    // The echo child. `args` is its parsed entry-point argv (Windows: one CommandLineToArgvW parse;
    // POSIX: the execve argv), i.e. exactly what our native marshalling produced, round-tripped back.
    // Mode "argv" echoes those args as a UTF-8 JSON array; mode "env" treats them as variable NAMES and
    // echoes each looked-up value (a parallel array). Raw bytes are written straight to stdout (no
    // TextWriter newline/encoding translation), so the captured JSON is byte-exact.
    static let helperProgram =
        """using System.Text.Json;
if (args.Length == 0) { Console.Error.Write("missing mode"); return 2; }
using var stdout = Console.OpenStandardOutput();
switch (args[0])
{
    case "argv":
        stdout.Write(JsonSerializer.SerializeToUtf8Bytes(args[1..]));
        break;
    case "env":
        var names = args[1..];
        var values = new string?[names.Length];
        for (int i = 0; i < names.Length; i++)
            values[i] = Environment.GetEnvironmentVariable(names[i]);
        stdout.Write(JsonSerializer.SerializeToUtf8Bytes(values));
        break;
    default:
        Console.Error.Write("unknown mode");
        return 3;
}
stdout.Flush();
return 0;
"""

    let mutable helperDir = ""
    let mutable helperDll = ""

    // Run the built echo child once and return the JSON array it emitted, decoded back into strings. A
    // failure to spawn/parse is an infrastructure fault (not a property counter-example), so it raises
    // rather than returning a value the property could mistake for a mismatch.
    member private _.Echo (mode: string) (envVars: (string * string) list) (items: string list) : string[] =
        let command =
            (Command.create "dotnet"
             |> Command.args ([ "exec"; helperDll; mode ] @ items)
             |> Command.timeout (TimeSpan.FromSeconds 60.0),
             envVars)
            ||> List.fold (fun c (key, value) -> c |> Command.env key value)

        match command.OutputBytesAsync().GetAwaiter().GetResult() with
        | Error error -> failwith $"echo child (mode {mode}) failed to run: {error.Message}"
        | Ok result ->
            if not result.IsSuccess then
                failwith $"echo child (mode {mode}) exited with {result.Code}: {result.Combined}"

            // The serializer escapes non-ASCII as \uXXXX, so the output is pure-ASCII JSON and a UTF-8
            // decode is exact; Deserialize rebuilds the original UTF-16 strings, surrogate pairs and all.
            let json = Encoding.UTF8.GetString result.Stdout

            match JsonSerializer.Deserialize<string[]>(json) with
            | null -> failwith $"echo child (mode {mode}) produced no JSON array: {json}"
            | echoed -> echoed

    [<OneTimeSetUp>]
    member _.BuildEchoChild() =
        let dir =
            Path.Combine(Path.GetTempPath(), "pk-argvenv-" + Guid.NewGuid().ToString "N")

        Directory.CreateDirectory dir |> ignore
        File.WriteAllText(Path.Combine(dir, "pkecho.csproj"), helperCsproj)
        File.WriteAllText(Path.Combine(dir, "Program.cs"), helperProgram)
        // Empty MSBuild-directory stoppers so a stray Directory.Build.props/.targets somewhere up the
        // temp path can never bleed unexpected settings into this isolated build.
        File.WriteAllText(Path.Combine(dir, "Directory.Build.props"), "<Project />")
        File.WriteAllText(Path.Combine(dir, "Directory.Build.targets"), "<Project />")

        let build () =
            // -nodeReuse:false + UseSharedCompilation=false side-step the MSBuild node / Roslyn build
            // server file-lock races this host has seen under concurrent worktree builds (KB K-014).
            // The MSBuild* env vars are stripped so a nested build launched from inside `dotnet test`
            // does not inherit the parent invocation's MSBuild toolset paths (a classic nested-build
            // footgun). DOTNET_NOLOGO/telemetry keep stdout free of first-run noise.
            (Command.create "dotnet"
             |> Command.args
                 [ "build"
                   Path.Combine(dir, "pkecho.csproj")
                   "-c"
                   "Release"
                   "--nologo"
                   "-v"
                   "quiet"
                   "-nodeReuse:false"
                   "-p:UseSharedCompilation=false" ]
             |> Command.env "DOTNET_NOLOGO" "1"
             |> Command.env "DOTNET_CLI_TELEMETRY_OPTOUT" "1"
             |> Command.envRemove "MSBUILD_EXE_PATH"
             |> Command.envRemove "MSBuildSDKsPath"
             |> Command.envRemove "MSBuildExtensionsPath"
             |> Command.envRemove "MSBUILDEXTENSIONSPATH"
             |> Command.envRemove "MSBuildLoadMicrosoftTargetsReadOnly"
             |> Command.timeout (TimeSpan.FromMinutes 3.0))
                .OutputStringAsync()
                .GetAwaiter()
                .GetResult()

        // One retry on a transient build failure (KB K-014: MSBuild locks / disk-space blips self-heal).
        let result =
            match build () with
            | Ok r when r.IsSuccess -> Ok r
            | _ ->
                System.Threading.Thread.Sleep 1500
                build ()

        match result with
        | Ok r when r.IsSuccess -> ()
        | Ok r -> Assert.Fail $"building the echo child failed (exit {r.Code}):\n{r.Combined}"
        | Error error -> Assert.Fail $"building the echo child failed: {error.Message}"

        let dll = Path.Combine(dir, "bin", "Release", "net10.0", "pkecho.dll")

        if not (File.Exists dll) then
            Assert.Fail $"the echo child built but its assembly is missing at {dll}"

        helperDir <- dir
        helperDll <- dll

    [<OneTimeTearDown>]
    member _.RemoveEchoChild() =
        if helperDir <> "" && Directory.Exists helperDir then
            try
                Directory.Delete(helperDir, true)
            with :? IOException ->
                // A build artifact may still be transiently locked (Windows) as the runtime unwinds; it
                // is a temp directory the OS reclaims anyway, so a failed cleanup must not fail the run.
                ()

    // --- argv round-trip -----------------------------------------------------------------------

    [<Test>]
    member this.``argv round-trips the specific adversarial shapes exactly through a real child``() =
        let cases =
            [ "" // empty argument
              "plain"
              "with space"
              "with\ttab"
              "with\nnewline"
              "embedded\"quote" // a quote inside the argument
              "\"fully quoted\""
              "back\\slash"
              "trailing\\" // a single trailing backslash
              "run\\\\\\three" // an interior run of backslashes
              "slash\\\"quote" // a backslash immediately before a quote
              "ends\\\\" // a trailing run of backslashes
              "/leading/and/trailing/slashes/" // slashes, including a trailing one
              "50%^&stuff" // cmd.exe / batch-significant characters
              "%PATH%"
              "bmp-é-中-€"
              "nonbmp-" + Char.ConvertFromUtf32 0x1F600 + Char.ConvertFromUtf32 0x1D7D8 ]

        let echoed = this.Echo "argv" [] cases
        // Explicit type argument picks the single `'T` overload of Is.EqualTo (a bare `string[]` is
        // otherwise an ambiguous match for both its array and `'T` overloads under F#); NUnit still
        // compares the two arrays element-wise, in order.
        Assert.That(echoed, Is.EqualTo<string[]>(List.toArray cases))

    [<Test>]
    member this.``argv round-trips arbitrary generated vectors through a real child``() =
        let property =
            Prop.forAll (Arb.fromGen argVectorGen) (fun (argv: string list) ->
                this.Echo "argv" [] argv = List.toArray argv)

        Check.One(Config.QuickThrowOnFailure.WithMaxTest iterations, property)

    // --- env round-trip ------------------------------------------------------------------------

    [<Test>]
    member this.``env round-trips the specific adversarial values exactly through a real child``() =
        let values =
            [ "plain"
              "with spaces"
              "with\ttab"
              "=leading=equals" // a value that itself starts with '='
              "has=an=equals=sign"
              "trailing space "
              "quote\"inside"
              "back\\slash\\value"
              "bmp-é-中-€"
              "nonbmp-" + Char.ConvertFromUtf32 0x1F680 + Char.ConvertFromUtf32 0x2F81A ]

        let names = values |> List.mapi (fun i _ -> $"PK_RT_ENV_{i}")
        let echoed = this.Echo "env" (List.zip names values) names
        Assert.That(echoed, Is.EqualTo<string[]>(List.toArray values))

    [<Test>]
    member this.``env round-trips arbitrary generated values through a real child``() =
        let property =
            Prop.forAll (Arb.fromGen envValuesGen) (fun (values: string list) ->
                let names = values |> List.mapi (fun i _ -> $"PK_RT_ENV_{i}")
                this.Echo "env" (List.zip names values) names = List.toArray values)

        Check.One(Config.QuickThrowOnFailure.WithMaxTest iterations, property)
