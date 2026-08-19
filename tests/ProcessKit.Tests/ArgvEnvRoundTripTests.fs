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

    static let isWindows = OperatingSystem.IsWindows()

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

    // --- Arg0: adapting the test values to the `/bin/sh` actually installed ---------------------

    // `/bin/sh` is normally `dash` or `bash`, which merely REPORT argv[0] (as `$0`), so the Arg0 tests
    // below can hand it any string. On a musl/Alpine image `/bin/sh` is instead a symlink to BusyBox, a
    // MULTICALL binary whose entry point DISPATCHES on argv[0]: it strips one leading `-`, takes the
    // basename, and looks the result up as an applet name before any shell — or `-c` — logic runs. An
    // unknown name exits 127 with "<name>: applet not found" and no shell ever starts. (That refusal is
    // itself evidence the override reached the exec'd child, but it is not an observation of it.) Which
    // argv[0] values are observable is therefore a property of the runtime environment, derived from it
    // here rather than hardcoded.
    static let arg0Probe = "pk-argv0-dispatch-probe"

    // Measured, not guessed from the binary's name or link target (a multicall `/bin/sh` can equally be a
    // symlink, a hard link or a copy): spawn `/bin/sh` once with an argv[0] that is deliberately not an
    // applet name. A byte-exact echo means an ordinary shell that only reports argv[0]; anything else
    // means this `/bin/sh` acts on argv[0]. A misclassification cannot hide a real `Arg0` regression —
    // the adapted values still assert exact equality, so an override that stopped reaching the child
    // fails there rather than being absorbed here.
    static let shellDispatchesOnArg0 =
        lazy
            (let probe =
                Command.create "/bin/sh"
                |> Command.arg0 arg0Probe
                |> Command.args [ "-c"; "printf %s \"$0\"" ]
                |> Command.timeout (TimeSpan.FromSeconds 30.0)

             match probe.RunAsync().GetAwaiter().GetResult() with
             | Ok observed -> observed <> arg0Probe
             | Error _ -> true)

    // Make one argv[0] value dispatchable by the `/bin/sh` in play WITHOUT shortening or simplifying it.
    // Only the basename decides the applet, so a `/sh` suffix routes to the multicall binary's own `sh`
    // applet — which a multicall binary installed AS `/bin/sh` necessarily provides, or nothing on the
    // image could run `sh` at all — while the whole original string still travels in argv[0]: BusyBox
    // massages only its private dispatch name and hands the applet the untouched argv, so `$0` still
    // yields the value byte-for-byte. That is reproducible on any BusyBox `/bin/sh` without .NET:
    // `sh -c 'exec -a "x/sh" /bin/sh -c "printf %s \"\$0\""'` prints `x/sh`, while the same line with
    // `-a "x"` prints `x: applet not found`. Ordinary shells, where argv[0] is inert, get it unchanged.
    static let dispatchableArg0 (value: string) =
        if shellDispatchesOnArg0.Value then value + "/sh" else value

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

    [<Test>]
    member _.``WindowsRawArg reaches a non-MSVCRT parser verbatim and POSIX refuses it``() =
        let command =
            Command.create "dotnet"
            |> Command.args [ "exec"; helperDll; "argv" ]
            |> Command.windowsRawArg "\"raw one\" raw-two"
            |> Command.timeout (TimeSpan.FromSeconds 60.0)

        match command.OutputBytesAsync().GetAwaiter().GetResult() with
        | Error(ProcessError.Unsupported _) when not isWindows -> ()
        | Error error -> Assert.Fail $"raw-argument echo failed: {error}"
        | Ok _ when not isWindows -> Assert.Fail "POSIX silently accepted a Windows raw command-line fragment"
        | Ok result ->
            let echoed =
                JsonSerializer.Deserialize<string[]>(Encoding.UTF8.GetString result.Stdout)

            Assert.That(echoed, Is.EqualTo<string[]>([| "raw one"; "raw-two" |]))

    // --- Arg0 (POSIX argv[0] override) round-trip -----------------------------------------------

    // `Arg0` overrides `argv[0]` of the spawned process itself, which the .NET echo child above cannot
    // observe (`Main(string[] args)` never includes argv[0]). `/bin/sh -c 'printf %s "$0"'` is the
    // portable POSIX way to read a process's OWN argv[0] back: with no `command_name` operand after the
    // script, POSIX sets `$0` from the shell's own `argv[0]` — exactly the element our native marshalling
    // overrides — so this observes the real `posix_spawnp` argv, not a re-quoted proxy. On a multicall
    // `/bin/sh` argv[0] is not inert, so the values are adapted to it — see `dispatchableArg0` above.
    [<Test>]
    member _.``Arg0 overrides the observed argv[0] of a real POSIX child (ASCII and non-ASCII)``() =
        if isWindows then
            Assert.Ignore "Arg0 is POSIX-only; see the Windows Unsupported test below"
        else
            let cases =
                [ "multicall-name" // a BusyBox/Toybox-style applet dispatch name
                  "-bash" // the login-shell leading-dash convention
                  "bmp-é-中-€" // BMP Unicode
                  "nonbmp-" + Char.ConvertFromUtf32 0x1F600 ] // a surrogate-pair code point
                // A leading dash is the one shape a multicall `/bin/sh` cannot report cleanly: the
                // dispatcher strips it, and the shell it then dispatches to reads the surviving dash as
                // "login shell" and sources /etc/profile (and $HOME/.profile) BEFORE running `-c`, so any
                // output of those files would arrive ahead of the value under test. Ordinary shells —
                // every non-musl leg — keep the case and cover the convention there.
                |> List.filter (fun case -> not (shellDispatchesOnArg0.Value && case.StartsWith '-'))
                |> List.map dispatchableArg0

            for case in cases do
                let command =
                    Command.create "/bin/sh"
                    |> Command.arg0 case
                    |> Command.args [ "-c"; "printf %s \"$0\"" ]
                    |> Command.timeout (TimeSpan.FromSeconds 30.0)

                match command.RunAsync().GetAwaiter().GetResult() with
                | Error error -> Assert.Fail $"Arg0 round-trip failed for '{case}': {error.Message}"
                | Ok observed -> Assert.That(observed, Is.EqualTo case, $"case: {case}")

    // A lone `Setsid` does not route through `setpriv`/`setsid --ctty`/a helper launcher, so it is the
    // one knob `Command.Arg0` composes with normally (T-376/R-03; the matrix row in
    // docs/platform-support.md and the comment in PosixSpawnCleanupTests.fs both promise this). `Setsid`
    // is a native `posix_spawn` attribute (`POSIX_SPAWN_SETSID`), a different code path from the plain
    // spawn the test above already exercises, so this observes the real combination rather than assuming
    // the two knobs are independent.
    [<Test>]
    member _.``Arg0 composes normally with a lone Setsid (POSIX_SPAWN_SETSID, no privilege drop)``() =
        if isWindows then
            Assert.Ignore "Arg0/Setsid are both POSIX-only"
        else
            let case = dispatchableArg0 "multicall-name"

            let command =
                Command.create "/bin/sh"
                |> Command.arg0 case
                |> Command.setsid
                |> Command.args [ "-c"; "printf %s \"$0\"" ]

            match command.RunAsync().GetAwaiter().GetResult() with
            | Error error -> Assert.Fail $"Arg0+Setsid round-trip failed: {error.Message}"
            | Ok observed -> Assert.That(observed, Is.EqualTo case)

    [<Test>]
    member _.``Arg0 on Windows is a typed Unsupported, never a silent fallback to Program``() =
        if not isWindows then
            Assert.Ignore
                "Arg0's Windows refusal is tested only on Windows; POSIX honors it (see the round-trip test above)"
        else
            let command =
                Command.create "cmd.exe"
                |> Command.arg0 "override"
                |> Command.args [ "/c"; "exit 0" ]

            match command.RunUnitAsync().GetAwaiter().GetResult() with
            | Error(ProcessError.Unsupported _) -> ()
            | Error other -> Assert.Fail $"expected ProcessError.Unsupported on Windows, got {other}"
            | Ok() -> Assert.Fail "Windows silently accepted a POSIX argv[0] override"

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
