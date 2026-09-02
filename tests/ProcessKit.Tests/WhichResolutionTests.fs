namespace ProcessKit.Tests

open System
open System.IO
open System.Runtime.InteropServices
open System.Threading
open System.Threading.Tasks
open NUnit.Framework
open ProcessKit
open ProcessKit.Native

/// The real POSIX bare-name launch is `posix_spawnp`, whose libc `PATH` search reads the native
/// `environ` (via `getenv`). .NET's managed `Environment.SetEnvironmentVariable` updates only the
/// runtime's own environment view — `Environment.GetEnvironmentVariable`/`GetEnvironmentVariables`, and
/// therefore `Exec.which`/`findInPath`, all observe it — but it does NOT write the native `environ`, so a
/// `PATH` entry added only through the managed API is invisible to `posix_spawnp`'s own search. A test
/// that needs the OS's real bare-name launch to find a tool on a temporarily-augmented `PATH` must set
/// the native `environ` directly with `setenv(3)`. POSIX-only: declared here but called solely under
/// `not isWindows` (Windows `CreateProcessW` reads the managed-updated block, so the managed set alone
/// suffices there, and there is no libc `setenv` to bind).
module private NativeEnv =

    [<DllImport("libc", CharSet = CharSet.Ansi, SetLastError = true)>]
    extern int setenv(string name, string value, int overwrite)

    [<DllImport("libc", CharSet = CharSet.Ansi, SetLastError = true)>]
    extern int unsetenv(string name)

    [<DllImport("libc", SetLastError = true)>]
    extern int geteuid()

/// Covers `Exec.which` / `CliClient.EnsureAvailableAsync` — the no-spawn preflight helper — and its
/// contract with the real spawn path: both go through the SAME PATH/PATHEXT-aware resolution
/// (`Common.resolveProgram`), so for the same program name they must always agree on
/// found-vs-not-found, and a bare-name miss's `Searched` diagnostic must match what a real spawn's
/// `NotFound` now reports too (spawn enrichment added alongside `which` in this change). Sequential
/// (no `[<Parallelizable>]`): the PATHEXT test temporarily mutates the process `PATH` env var.
[<TestFixture>]
type WhichResolutionTests() =

    let isWindows = RuntimeInformation.IsOSPlatform OSPlatform.Windows

    // A bare name guaranteed absent from PATH — mirrors the existing real-spawn NotFound coverage in
    // ProcessErrorAccessorTests (C#), reused here to cross-check against `which`.
    let missingProgram = "this-program-does-not-exist-xyz"

    // A bare name every target OS resolves via PATH/PATHEXT without an explicit extension: `cmd` on
    // Windows (found via the default PATHEXT's `.EXE`), `sh` on POSIX (a plain executable bit, no
    // PATHEXT concept).
    let presentBareProgram = if isWindows then "cmd" else "sh"

    // A fresh, unique temp directory for a prefer-local test (`Command.PreferLocal`, T-182).
    let freshDir (tag: string) =
        let dir =
            Path.Combine(Path.GetTempPath(), "processkit-preferlocal-" + tag + "-" + Guid.NewGuid().ToString "N")

        Directory.CreateDirectory dir |> ignore
        dir

    let setProcessPath (value: string) =
        Environment.SetEnvironmentVariable("PATH", value)

        if not isWindows then
            NativeEnv.setenv ("PATH", value, 1) |> ignore

    [<Test>]
    member _.``which reports NotFound with Searched for a bare name absent from PATH``() =
        match Exec.which missingProgram with
        | Ok path -> Assert.Fail $"expected NotFound, but resolved to '{path}'"
        | Error error ->
            Assert.That(error.IsNotFound, Is.True)

            match error with
            | ProcessError.NotFound(program, searched) ->
                Assert.That(program, Is.EqualTo missingProgram)

                // PATH is virtually always set in a test/CI environment; when it is, `which` must name
                // exactly that raw value (the same diagnostic a real spawn's NotFound now carries too —
                // see the agreement test below).
                match Environment.GetEnvironmentVariable "PATH" with
                | null
                | "" -> ()
                | path -> Assert.That(searched, Is.EqualTo(Some path))
            | other -> Assert.Fail $"expected NotFound, got {other}"

    [<Test>]
    member _.``which and a real spawn agree: a missing bare-name program is NotFound on both paths``() : Task =
        task {
            let whichResult = Exec.which missingProgram

            let! spawnResult = (Command.create missingProgram).OutputStringAsync()

            match whichResult, spawnResult with
            | Error whichError, Error spawnError ->
                Assert.That(whichError.IsNotFound, Is.True)
                Assert.That(spawnError.IsNotFound, Is.True)

                // Both derive `Searched` from the very same `findInPath` walk, so a real spawn's
                // enrichment and the standalone preflight helper must report the identical diagnostic.
                match whichError, spawnError with
                | ProcessError.NotFound(_, whichSearched), ProcessError.NotFound(_, spawnSearched) ->
                    Assert.That(spawnSearched, Is.EqualTo whichSearched)
                | _ -> Assert.Fail "expected both errors to be NotFound"
            | other -> Assert.Fail $"expected both which and spawn to fail as NotFound, got {other}"
        }
        :> Task

    [<Test>]
    member _.``which and a real spawn agree: an existing bare-name program is found on both paths``() : Task =
        task {
            match Exec.which presentBareProgram with
            | Error error -> Assert.Fail $"expected '{presentBareProgram}' to resolve, got {error}"
            | Ok resolvedPath ->
                Assert.That(File.Exists resolvedPath, Is.True)

                // The bare name itself must still be spawnable through the ordinary OS resolution path
                // (proving `which`'s answer and the real spawn's own PATH search agree on "found").
                let bareCommand =
                    if isWindows then
                        Command.create presentBareProgram |> Command.args [ "/c"; "exit 0" ]
                    else
                        Command.create presentBareProgram |> Command.args [ "-c"; "exit 0" ]

                let! bareResult = bareCommand.OutputStringAsync()

                match bareResult with
                | Error error -> Assert.Fail $"expected the bare-name spawn to succeed, got {error}"
                | Ok _ -> ()

                // `which`'s resolved full path is itself a directly-executable file: spawning it
                // verbatim must also succeed — the "same final path" half of the agreement.
                let resolvedCommand =
                    if isWindows then
                        Command.create resolvedPath |> Command.args [ "/c"; "exit 0" ]
                    else
                        Command.create resolvedPath |> Command.args [ "-c"; "exit 0" ]

                let! resolvedResult = resolvedCommand.OutputStringAsync()

                match resolvedResult with
                | Error error -> Assert.Fail $"expected the resolved-path spawn to succeed, got {error}"
                | Ok _ -> ()
        }
        :> Task

    [<Test>]
    member _.``Windows: which and StartAsync agree for a bare name found only in the current directory``() : Task =
        task {
            if not isWindows then
                Assert.Ignore "CreateProcessW's current-directory bare-name search is Windows-only."
            else
                let dir =
                    Path.Combine(Path.GetTempPath(), "processkit-cwd-resolution-" + Guid.NewGuid().ToString "N")

                Directory.CreateDirectory dir |> ignore
                let toolName = "pk320-cwd-tool"
                let toolPath = Path.Combine(dir, toolName + ".exe")
                let originalDirectory = Directory.GetCurrentDirectory()
                let originalPath = Environment.GetEnvironmentVariable "PATH"

                let originalCurrentDirectoryPolicy =
                    Environment.GetEnvironmentVariable "NoDefaultCurrentDirectoryInExePath"

                try
                    File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), toolPath)
                    Environment.SetEnvironmentVariable("PATH", Environment.SystemDirectory)
                    Environment.SetEnvironmentVariable("NoDefaultCurrentDirectoryInExePath", null)
                    Directory.SetCurrentDirectory dir

                    match Exec.which toolName with
                    | Error error -> Assert.Fail $"expected cwd-only program to resolve, got {error}"
                    | Ok resolved -> Assert.That(resolved, Is.EqualTo(Path.GetFullPath toolPath).IgnoreCase)

                    match! (Command.create toolName |> Command.args [ "/c"; "exit 0" ]).StartAsync() with
                    | Error error -> Assert.Fail $"expected cwd-only program to start, got {error}"
                    | Ok running ->
                        let! outcome = running.WaitAsync()
                        Assert.That(outcome, Is.EqualTo(Outcome.Exited 0))
                finally
                    Directory.SetCurrentDirectory originalDirectory
                    Environment.SetEnvironmentVariable("PATH", originalPath)

                    Environment.SetEnvironmentVariable(
                        "NoDefaultCurrentDirectoryInExePath",
                        originalCurrentDirectoryPolicy
                    )

                    Directory.Delete(dir, true)
        }
        :> Task

    [<Test>]
    member _.``Windows: which and StartAsync agree when NoDefaultCurrentDirectoryInExePath excludes the current directory``
        ()
        : Task =
        task {
            if not isWindows then
                Assert.Ignore "NeedCurrentDirectoryForExePathW is Windows-only."
            else
                let dir =
                    Path.Combine(Path.GetTempPath(), "processkit-cwd-policy-" + Guid.NewGuid().ToString "N")

                Directory.CreateDirectory dir |> ignore
                let toolName = "pk320-cwd-policy"
                let toolPath = Path.Combine(dir, toolName + ".exe")
                let originalDirectory = Directory.GetCurrentDirectory()
                let originalPath = Environment.GetEnvironmentVariable "PATH"

                let originalCurrentDirectoryPolicy =
                    Environment.GetEnvironmentVariable "NoDefaultCurrentDirectoryInExePath"

                try
                    File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), toolPath)
                    Environment.SetEnvironmentVariable("PATH", Environment.SystemDirectory)
                    Environment.SetEnvironmentVariable("NoDefaultCurrentDirectoryInExePath", "1")
                    Directory.SetCurrentDirectory dir

                    let whichResult = Exec.which toolName

                    match! (Command.create toolName |> Command.args [ "/c"; "exit 0" ]).StartAsync() with
                    | Ok running ->
                        do! (running :> IAsyncDisposable).DisposeAsync()
                        Assert.Fail "expected the current-directory policy to exclude the cwd-only program"
                    | Error spawnError ->
                        Assert.That(spawnError.IsNotFound, Is.True, $"expected NotFound, got {spawnError}")

                        match whichResult with
                        | Error whichError ->
                            Assert.That(whichError.IsNotFound, Is.True, $"expected which NotFound, got {whichError}")
                        | Ok resolved -> Assert.Fail $"which unexpectedly resolved '{resolved}'"
                finally
                    Directory.SetCurrentDirectory originalDirectory
                    Environment.SetEnvironmentVariable("PATH", originalPath)

                    Environment.SetEnvironmentVariable(
                        "NoDefaultCurrentDirectoryInExePath",
                        originalCurrentDirectoryPolicy
                    )

                    Directory.Delete(dir, true)
        }
        :> Task

    [<Test>]
    member _.``Windows: which and StartAsync agree when a bare name is absent from the current directory and PATH``
        ()
        : Task =
        task {
            if not isWindows then
                Assert.Ignore "CreateProcessW's current-directory bare-name search is Windows-only."
            else
                let dir =
                    Path.Combine(Path.GetTempPath(), "processkit-cwd-missing-" + Guid.NewGuid().ToString "N")

                Directory.CreateDirectory dir |> ignore
                let toolName = "pk320-cwd-missing"
                let originalDirectory = Directory.GetCurrentDirectory()
                let originalPath = Environment.GetEnvironmentVariable "PATH"

                try
                    Environment.SetEnvironmentVariable("PATH", Environment.SystemDirectory)
                    Directory.SetCurrentDirectory dir

                    let whichResult = Exec.which toolName

                    match! (Command.create toolName).StartAsync() with
                    | Ok running ->
                        do! (running :> IAsyncDisposable).DisposeAsync()
                        Assert.Fail "expected a cwd/PATH miss, but StartAsync launched the program"
                    | Error spawnError ->
                        Assert.That(spawnError.IsNotFound, Is.True, $"expected NotFound, got {spawnError}")

                        match whichResult with
                        | Error whichError ->
                            Assert.That(whichError.IsNotFound, Is.True, $"expected which NotFound, got {whichError}")
                        | Ok resolved -> Assert.Fail $"which unexpectedly resolved '{resolved}'"
                finally
                    Directory.SetCurrentDirectory originalDirectory
                    Environment.SetEnvironmentVariable("PATH", originalPath)
                    Directory.Delete(dir, true)
        }
        :> Task

    [<Test>]
    member _.``which resolves a path-form program directly, with no PATH search, and no Searched on a miss``() =
        let dir =
            Path.Combine(Path.GetTempPath(), "processkit-which-" + Guid.NewGuid().ToString "N")

        Directory.CreateDirectory dir |> ignore

        try
            let exeName = if isWindows then "pk105-tool.exe" else "pk105-tool"
            let exePath = Path.Combine(dir, exeName)

            if isWindows then
                // A trivial valid PE isn't needed: `which` only checks for the file's existence at the
                // exact path-form candidate (no PATHEXT search for an already-extensioned path-form
                // program), so any bytes suffice.
                File.WriteAllText(exePath, "not a real PE, existence is enough for resolution")
            else
                File.WriteAllText(exePath, "#!/bin/sh\nexit 0\n")
                File.SetUnixFileMode(exePath, UnixFileMode.UserRead ||| UnixFileMode.UserExecute)

            match Common.resolveProgram exePath with
            | Ok resolved -> Assert.That(resolved, Is.EqualTo exePath)
            | Error error -> Assert.Fail $"expected the path-form program to resolve, got {error}"

            // A missing path-form program carries no `Searched` — nothing was searched, a single
            // candidate location was checked.
            let missingPathForm = Path.Combine(dir, "does-not-exist-here")

            match Common.resolveProgram missingPathForm with
            | Ok found -> Assert.Fail $"expected NotFound, but resolved to '{found}'"
            | Error(ProcessError.NotFound(program, searched)) ->
                Assert.That(program, Is.EqualTo missingPathForm)
                Assert.That(searched, Is.EqualTo None)
            | Error other -> Assert.Fail $"expected NotFound, got {other}"
        finally
            Directory.Delete(dir, true)

    [<Test>]
    member _.``Windows PATHEXT: an extension-omitted bare name resolves via its .exe sibling ahead of a same-named .bat, and agrees with a real spawn``
        ()
        : Task =
        task {
            if not isWindows then
                Assert.Ignore "PATHEXT is a Windows-only concept; POSIX resolution is covered above."
            else
                let dir =
                    Path.Combine(Path.GetTempPath(), "processkit-pathext-" + Guid.NewGuid().ToString "N")

                Directory.CreateDirectory dir |> ignore
                let toolName = "pk105-pathext-tool"

                // A `.bat` sibling that would fail the run if it were the one actually picked — proving
                // resolution order matters, not just "some sibling matched". The `.exe` sibling is a
                // genuine copy of `cmd.exe` (a real, directly-executable PE — a hand-written "stub" file
                // would fail with a bad-exe-format `Spawn` error, defeating the "agrees with a real
                // spawn" half of this test) renamed with no shell involved, so it is spawnable exactly
                // like any other bare-name `.exe` resolution.
                let batPath = Path.Combine(dir, toolName + ".bat")
                File.WriteAllText(batPath, "@echo off\r\nexit /b 1\r\n")

                let systemCmdExe =
                    Path.Combine(Environment.GetFolderPath Environment.SpecialFolder.System, "cmd.exe")

                let exePath = Path.Combine(dir, toolName + ".exe")
                File.Copy(systemCmdExe, exePath)

                let originalPath = Environment.GetEnvironmentVariable "PATH"

                try
                    Environment.SetEnvironmentVariable("PATH", dir + ";" + originalPath)

                    match Exec.which toolName with
                    | Error error -> Assert.Fail $"expected '{toolName}' to resolve via its .exe sibling, got {error}"
                    | Ok resolved ->
                        // Default PATHEXT (`.COM;.EXE;.BAT;.CMD`) probes `.exe` before `.bat` — the
                        // `.bat` sibling must never win.
                        Assert.That(resolved, Is.EqualTo(exePath).IgnoreCase)

                        // The same bare name must also be spawnable through the real OS resolution
                        // (which only auto-appends `.exe`, never searches `.bat`/`.cmd` itself) — `which`
                        // and the real spawn agree on both "found" and "which file".
                        let! spawnResult =
                            (Command.create toolName |> Command.args [ "/c"; "exit 0" ]).OutputStringAsync()

                        match spawnResult with
                        | Error error -> Assert.Fail $"expected '{toolName}' to spawn successfully, got {error}"
                        | Ok result -> Assert.That(result.Code, Is.EqualTo(Some 0))
                finally
                    Environment.SetEnvironmentVariable("PATH", originalPath)
                    Directory.Delete(dir, true)
        }
        :> Task

    [<Test>]
    member _.``which and a real spawn agree: a program reachable only via a quoted PATH entry is NotFound on both``
        ()
        : Task =
        task {
            if not isWindows then
                Assert.Ignore "Quoting individual PATH entries is a Windows-only convention."
            else
                // A directory named with a space AND an embedded ';' — the two reasons a Windows PATH entry
                // gets wrapped in double quotes. Copy a genuine, directly-launchable PE (`cmd.exe`) into it,
                // so the tool itself is unimpeachably valid and the only thing making it unreachable below is
                // the quoting of its PATH entry, not a bad file.
                let dir =
                    Path.Combine(Path.GetTempPath(), "processkit quoted;path-" + Guid.NewGuid().ToString "N")

                Directory.CreateDirectory dir |> ignore
                let toolName = "pk127-quoted-path-tool"
                let exePath = Path.Combine(dir, toolName + ".exe")

                let systemCmdExe =
                    Path.Combine(Environment.GetFolderPath Environment.SpecialFolder.System, "cmd.exe")

                File.Copy(systemCmdExe, exePath)

                let originalPath = Environment.GetEnvironmentVariable "PATH"

                try
                    // Control: addressed by its full path (path-form, no PATH search), the copied exe is a
                    // real, launchable program — so the NotFound verdicts below are about the quoting, not a
                    // broken tool. A quoted executable path in the command line IS honoured by CreateProcessW
                    // (that is command-line parsing, distinct from PATH-entry quoting, which is not).
                    let! byFullPath = (Command.create exePath |> Command.args [ "/c"; "exit 0" ]).OutputStringAsync()

                    match byFullPath with
                    | Error error -> Assert.Fail $"expected the tool to launch by full path, got {error}"
                    | Ok result -> Assert.That(result.Code, Is.EqualTo(Some 0))

                    // Now expose the tool ONLY through a double-quoted PATH entry. Neither CreateProcessW
                    // (lpApplicationName = NULL, the real bare-name launch) nor SearchPathW strips the
                    // surrounding quotes, so the actual spawn cannot reach the tool this way. `Exec.which`
                    // shares `findInPath`, so it MUST reach the same verdict — a `which` "found" here would be
                    // exactly the preflight/spawn divergence this resolver exists to prevent, inverted.
                    let quotedDirectory = $"\"{dir}\""

                    let updatedPath =
                        if String.IsNullOrEmpty originalPath then
                            quotedDirectory
                        else
                            quotedDirectory + ";" + originalPath

                    Environment.SetEnvironmentVariable("PATH", updatedPath)

                    let whichResult = Exec.which toolName

                    let! spawnResult = (Command.create toolName |> Command.args [ "/c"; "exit 0" ]).OutputStringAsync()

                    match whichResult, spawnResult with
                    | Error whichError, Error spawnError ->
                        Assert.That(whichError.IsNotFound, Is.True, $"which should be NotFound, got {whichError}")
                        Assert.That(spawnError.IsNotFound, Is.True, $"spawn should be NotFound, got {spawnError}")
                    | Ok resolved, _ ->
                        Assert.Fail
                            $"which resolved '{toolName}' to '{resolved}' via a quoted PATH entry, but the real spawn cannot launch it that way — a preflight/spawn divergence"
                    | _, Ok _ ->
                        Assert.Fail
                            "the real spawn unexpectedly launched a tool reachable only through a quoted PATH entry"
                finally
                    Environment.SetEnvironmentVariable("PATH", originalPath)
                    Directory.Delete(dir, true)
        }
        :> Task

    [<Test>]
    member _.``probeDir survives a candidate disappearing between the existence check and the executable-bit check``() =
        if isWindows then
            Assert.Ignore
                "the File.Exists -> File.GetUnixFileMode TOCTOU window is POSIX-only; Windows probing has no analogous race."
        else
            // There is no hook into `probeDir`'s internals to force the exact interleaving, so this
            // reproduces the race as deterministically as the test harness allows: a background thread
            // continuously creates/marks-executable/deletes the SAME candidate file while the main
            // thread hammers `probeDir` on it. Across enough iterations the two threads are virtually
            // certain to interleave such that `File.Exists` observes the file present just before the
            // churner thread deletes it, forcing `File.GetUnixFileMode` to race a `FileNotFoundException`
            // — the exact TOCTOU window `probeDir` must now absorb instead of letting escape.
            let dir =
                Path.Combine(Path.GetTempPath(), "processkit-toctou-" + Guid.NewGuid().ToString "N")

            Directory.CreateDirectory dir |> ignore
            let program = "pk134-toctou-tool"
            let candidate = Path.Combine(dir, program)

            use stop = new CancellationTokenSource()

            let churn () =
                while not stop.IsCancellationRequested do
                    try
                        File.WriteAllText(candidate, "x")
                        File.SetUnixFileMode(candidate, UnixFileMode.UserExecute)
                        File.Delete candidate
                    with
                    | :? IOException
                    | :? UnauthorizedAccessException ->
                        // Expected background noise from racing our own writes/deletes against
                        // `probeDir`'s reads on the main thread — not the condition under test.
                        ()

            let churner = Task.Run churn

            try
                // 5000 iterations comfortably interleaves with the churner thread on every CI OS this
                // runs on (Linux/macOS) without making the test slow; the assertion is simply that none
                // of them throws, regardless of which candidate state `probeDir` happens to observe.
                for _ in 1..5000 do
                    // POSIX-only test (Ignored on Windows), so the PATHEXT source is irrelevant here — pass
                    // "" (the resolver's "unset" form) as the explicit PATHEXT the probe now takes.
                    Common.probeDir "" dir program |> ignore
            finally
                stop.Cancel()
                churner.Wait()

                try
                    File.Delete candidate
                with :? IOException ->
                    // may already be gone from the churner's own last delete - fine, nothing to clean up.
                    ()

                Directory.Delete(dir, true)

    [<Test>]
    member _.``Windows: a bare name whose only PATH match is a .cmd shim launches, agreeing with which (T-181)``
        ()
        : Task =
        task {
            if not isWindows then
                Assert.Ignore "PATHEXT / .cmd shim substitution is a Windows-only concept; POSIX has no PATHEXT."
            else
                let dir =
                    Path.Combine(Path.GetTempPath(), "processkit-cmdshim-" + Guid.NewGuid().ToString "N")

                Directory.CreateDirectory dir |> ignore
                let toolName = "pk181-cmd-shim"

                // A `.cmd` shim (like the `npm`/`yarn`/`az` wrappers) reachable ONLY by its `.cmd`
                // extension — no `.exe` sibling — so the OS's own bare-name PATH search, which appends only
                // `.exe`, would miss it entirely. Batch files require CRLF line endings.
                let cmdPath = Path.Combine(dir, toolName + ".cmd")
                File.WriteAllText(cmdPath, "@echo off\r\necho SHIM-OK\r\n")

                let originalPath = Environment.GetEnvironmentVariable "PATH"

                try
                    Environment.SetEnvironmentVariable("PATH", dir + ";" + originalPath)

                    // Preflight resolves the shim to its `.cmd`.
                    match Exec.which toolName with
                    | Error error -> Assert.Fail $"expected which to resolve the .cmd shim, got {error}"
                    | Ok resolved -> Assert.That(resolved, Is.EqualTo(cmdPath).IgnoreCase)

                    // The invariant this task closes: the SAME bare name which resolved must actually
                    // launch — not `ProcessError.NotFound` — because the spawn now substitutes the resolved
                    // absolute `.cmd` path and routes it through `cmd.exe /d /c`.
                    let! result = (Command.create toolName).OutputStringAsync()

                    match result with
                    | Error error -> Assert.Fail $"expected the .cmd shim to launch, got {error}"
                    | Ok output ->
                        Assert.That(output.Code, Is.EqualTo(Some 0))
                        Assert.That(output.Stdout, Does.Contain "SHIM-OK")
                finally
                    Environment.SetEnvironmentVariable("PATH", originalPath)
                    Directory.Delete(dir, true)
        }
        :> Task

    [<Test>]
    member _.``Windows: a cmd metacharacter argument reaches a .cmd child as one literal argument, with no injection (T-181)``
        ()
        : Task =
        task {
            if not isWindows then
                Assert.Ignore "cmd.exe argument quoting is only exercised on the Windows .cmd/.bat launch path."
            else
                let dir =
                    Path.Combine(Path.GetTempPath(), "processkit-cmdquote-" + Guid.NewGuid().ToString "N")

                Directory.CreateDirectory dir |> ignore
                let toolName = "pk181-cmd-quote"

                // Echo the first argument back, keeping its surrounding quotes (`%1`, not `%~1`) so any
                // metacharacter the argument carries stays inside cmd's quotes when the batch itself
                // re-parses the echo — the batch is a faithful mirror, never a second injection site.
                let cmdPath = Path.Combine(dir, toolName + ".cmd")
                File.WriteAllText(cmdPath, "@echo off\r\necho ARG=[%1]\r\n")

                let originalPath = Environment.GetEnvironmentVariable "PATH"

                try
                    Environment.SetEnvironmentVariable("PATH", dir + ";" + originalPath)

                    // An argument packed with cmd command-chaining metacharacters AND an `exit 9` that, if
                    // cmd interpreted it instead of passing it through, would break out and exit the wrapper
                    // with code 9. BatBadBut / CVE-2024-24576 in one string.
                    let hostileArg = "x && exit 9 && y"

                    let! result = (Command.create toolName |> Command.args [ hostileArg ]).OutputStringAsync()

                    match result with
                    | Error error -> Assert.Fail $"expected the quoted .cmd launch to succeed, got {error}"
                    | Ok output ->
                        // No injection: the wrapper exits with the batch's own success (0), NOT the injected
                        // `exit 9`.
                        Assert.That(
                            output.Code,
                            Is.EqualTo(Some 0),
                            "a cmd metacharacter argument must not inject a command"
                        )
                        // Literal delivery: the child received the whole string as one argument.
                        Assert.That(output.Stdout, Does.Contain hostileArg)
                finally
                    Environment.SetEnvironmentVariable("PATH", originalPath)
                    Directory.Delete(dir, true)
        }
        :> Task

    [<Test>]
    member _.``Windows: a .cmd argument that cannot be safely quoted for cmd.exe is an honest typed refusal (T-181)``
        ()
        : Task =
        task {
            if not isWindows then
                Assert.Ignore "The cmd.exe quoting refusal only applies to the Windows .cmd/.bat launch path."
            else
                let dir =
                    Path.Combine(Path.GetTempPath(), "processkit-cmdrefuse-" + Guid.NewGuid().ToString "N")

                Directory.CreateDirectory dir |> ignore
                let toolName = "pk181-cmd-refuse"
                let cmdPath = Path.Combine(dir, toolName + ".cmd")
                File.WriteAllText(cmdPath, "@echo off\r\necho REACHED\r\n")

                let originalPath = Environment.GetEnvironmentVariable "PATH"

                try
                    Environment.SetEnvironmentVariable("PATH", dir + ";" + originalPath)

                    // A percent sign expands on a cmd command line regardless of any caret/quote escaping,
                    // so it cannot be passed safely to a batch wrapper. The contract is an honest typed
                    // refusal, never a 'run it anyway'.
                    let! result = (Command.create toolName |> Command.args [ "hello%world" ]).OutputStringAsync()

                    match result with
                    | Ok output ->
                        Assert.Fail
                            $"expected a typed refusal for a percent-bearing batch argument, but the run succeeded: {output.Stdout}"
                    | Error(ProcessError.Spawn _) -> ()
                    | Error other -> Assert.Fail $"expected a ProcessError.Spawn refusal, got {other}"
                finally
                    Environment.SetEnvironmentVariable("PATH", originalPath)
                    Directory.Delete(dir, true)
        }
        :> Task

    [<Test>]
    member _.``WindowsRawArg refuses an automatically resolved batch wrapper``() : Task =
        task {
            if not isWindows then
                Assert.Ignore "Automatic .cmd/.bat wrapping is Windows-only."
            else
                let dir =
                    Path.Combine(Path.GetTempPath(), "processkit-rawbatch-" + Guid.NewGuid().ToString "N")

                Directory.CreateDirectory dir |> ignore
                let toolName = "raw-target"
                let script = Path.Combine(dir, toolName + ".cmd")
                File.WriteAllText(script, "@echo off\r\necho REACHED\r\n")
                let originalPath = Environment.GetEnvironmentVariable "PATH"

                try
                    Environment.SetEnvironmentVariable("PATH", dir + ";" + originalPath)

                    let! result =
                        (Command.create toolName |> Command.windowsRawArg "\"opaque fragment\"").OutputStringAsync()

                    match result with
                    | Error(ProcessError.Unsupported message) -> Assert.That(message, Does.Contain "cmd.exe explicitly")
                    | Error other -> Assert.Fail $"expected Unsupported, got {other}"
                    | Ok output -> Assert.Fail $"batch wrapper unexpectedly launched: {output.Stdout}"
                finally
                    Environment.SetEnvironmentVariable("PATH", originalPath)
                    Directory.Delete(dir, true)
        }
        :> Task

    // ---- Command.PreferLocal (T-182): prefer-local program resolution ---------------------------------
    //
    // A marker tool named `baseName` in `dir` (created if absent) that prints `marker`, then its own
    // resolved program path (POSIX `$0` / Windows `%~f0`) so a test can observe BOTH which tool ran and
    // the absolute path the OS was handed. Returns the bare name used to launch it. A Windows tool is a
    // `.cmd` shim (routed through cmd.exe, exactly the `node_modules/.bin` shape); a POSIX tool is an
    // executable `/bin/sh` script. The `%~f0` / `$0` lines are plain string literals, not interpolated,
    // so the `%`/`$` reach the batch/shell verbatim.
    member private _.WriteMarkerTool(dir: string, baseName: string, marker: string) : string =
        Directory.CreateDirectory dir |> ignore

        if isWindows then
            let cmdPath = Path.Combine(dir, baseName + ".cmd")
            File.WriteAllText(cmdPath, "@echo off\r\necho " + marker + "\r\necho ARGV0=%~f0\r\n")
        else
            let path = Path.Combine(dir, baseName)
            File.WriteAllText(path, "#!/bin/sh\necho " + marker + "\necho ARGV0=$0\n")

            File.SetUnixFileMode(path, UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute)

        baseName

    [<Test>]
    member this.``relative PATH entries resolve to absolute paths across public preflight APIs``() : Task =
        task {
            let dir = freshDir "relative-path"
            let toolName = "pk319-relative-path-tool"
            let originalPath = Environment.GetEnvironmentVariable "PATH"

            let restorePath =
                match originalPath with
                | null -> ""
                | value -> value

            try
                this.WriteMarkerTool(dir, toolName, "RELATIVE-PATH-HIT") |> ignore

                // The PATH entry is relative to the process cwd. On POSIX, update libc's environ too so
                // the real posix_spawnp bare-name search sees the same value as the managed resolver.
                let relativeDir = Path.GetRelativePath(Directory.GetCurrentDirectory(), dir)
                setProcessPath relativeDir

                let expectedPath =
                    Path.GetFullPath(Path.Combine(dir, if isWindows then toolName + ".cmd" else toolName))

                let assertResolved (label: string) (result: Result<string, ProcessError>) =
                    match result with
                    | Ok resolved -> Assert.That(resolved, Is.EqualTo(expectedPath).IgnoreCase, label)
                    | Error error -> Assert.Fail $"{label}: expected a resolution, got {error}"

                assertResolved "Exec.which" (Exec.which toolName)

                let command = Command.create toolName |> Command.env "PATH" relativeDir
                assertResolved "Command.ResolveProgram" (command.ResolveProgram())

                let client =
                    (CliClient.create toolName).WithDefaults(fun template -> template.Env("PATH", relativeDir))

                assertResolved "CliClient.ResolveProgram" (client.ResolveProgram())

                let! spawnResult = command.OutputStringAsync()

                match spawnResult with
                | Ok output -> Assert.That(output.Stdout, Does.Contain "RELATIVE-PATH-HIT")
                | Error error -> Assert.Fail $"relative PATH bare-name spawn failed: {error}"
            finally
                setProcessPath restorePath
                Directory.Delete(dir, true)
        }
        :> Task

    [<Test>]
    member this.``relative PATH entries anchor to the command's effective CurrentDir``() =
        let baseDir = freshDir "relative-path-current-dir"
        let toolsDir = Path.Combine(baseDir, "tools")
        let toolName = "pk319-current-dir-path-tool"

        try
            this.WriteMarkerTool(toolsDir, toolName, "CURRENT-DIR-PATH-HIT") |> ignore

            let command =
                Command.create toolName
                |> Command.currentDir baseDir
                |> Command.env "PATH" "tools"

            let expectedPath =
                Path.GetFullPath(Path.Combine(toolsDir, if isWindows then toolName + ".cmd" else toolName))

            match command.ResolveProgram() with
            | Ok resolved -> Assert.That(resolved, Is.EqualTo(expectedPath).IgnoreCase)
            | Error error -> Assert.Fail $"relative PATH did not use CurrentDir: {error}"

            let client =
                (CliClient.create toolName)
                    .WithDefaults(fun template -> template.CurrentDir(baseDir).Env("PATH", "tools"))

            match client.ResolveProgram() with
            | Ok resolved -> Assert.That(resolved, Is.EqualTo(expectedPath).IgnoreCase)
            | Error error -> Assert.Fail $"CliClient relative PATH did not use CurrentDir: {error}"
        finally
            Directory.Delete(baseDir, true)

    [<Test>]
    member this.``PreferLocal resolves a bare name absent from PATH and launches it (T-182)``() : Task =
        task {
            let dir = freshDir "absent"
            let toolName = "pk182-absent-tool"

            try
                this.WriteMarkerTool(dir, toolName, "LOCAL-HIT") |> ignore

                // Control: the bare name is NOT on PATH, so a plain launch is NotFound — proving the tool is
                // reachable ONLY through the prefer-local directory, not by any ambient PATH entry.
                let! control = (Command.create toolName).OutputStringAsync()

                match control with
                | Error error ->
                    Assert.That(error.IsNotFound, Is.True, $"expected NotFound without prefer-local, got {error}")
                | Ok output -> Assert.Fail $"expected NotFound without prefer-local, but it ran: {output.Stdout}"

                // With the prefer-local directory the same bare name resolves and launches.
                let! result = (Command.create toolName |> Command.preferLocal dir).OutputStringAsync()

                match result with
                | Error error -> Assert.Fail $"expected the prefer-local tool to launch, got {error}"
                | Ok output ->
                    Assert.That(output.Code, Is.EqualTo(Some 0))
                    Assert.That(output.Stdout, Does.Contain "LOCAL-HIT")
            finally
                Directory.Delete(dir, true)
        }
        :> Task

    [<Test>]
    member this.``PreferLocal takes priority over a same-named program on PATH (T-182)``() : Task =
        task {
            let localDir = freshDir "prio-local"
            let pathDir = freshDir "prio-path"
            let toolName = "pk182-prio-tool"
            let originalPath = Environment.GetEnvironmentVariable "PATH"

            // `GetEnvironmentVariable` is nullable; keep a non-null form for the native `setenv` restore
            // in `finally` (its `value` parameter is a non-nullable string, unlike managed
            // `SetEnvironmentVariable`, which accepts null to remove the variable).
            let restorePath =
                match originalPath with
                | null -> ""
                | value -> value

            try
                this.WriteMarkerTool(localDir, toolName, "FROM-LOCAL") |> ignore
                this.WriteMarkerTool(pathDir, toolName, "FROM-PATH") |> ignore

                // Put `pathDir` on the process `PATH` for BOTH the managed resolver AND the OS's own
                // bare-name launch. On Windows the managed set is enough — `CreateProcessW` reads the
                // updated block, and here the `.cmd` tool is resolved by ProcessKit's own `PATH` walk
                // (which reads the managed `Environment`) and substituted. On POSIX the real launch of the
                // plain executable is `posix_spawnp`, whose libc `PATH` search reads the native `environ`
                // that `Environment.SetEnvironmentVariable` does NOT update (it touches only the managed
                // view) — so `setenv(3)` writes it directly, making the tool genuinely reachable on the
                // `PATH` the spawn actually searches (see the `NativeEnv` note). Without the native write
                // the `viaPath` baseline below cannot find the tool on POSIX even though `which` resolves
                // it against the managed `PATH` — the exact managed/native divergence this test sets up.
                let augmentedPath = pathDir + string Path.PathSeparator + originalPath
                Environment.SetEnvironmentVariable("PATH", augmentedPath)

                if not isWindows then
                    NativeEnv.setenv ("PATH", augmentedPath, 1) |> ignore

                // Without prefer-local, PATH wins — proving the two directories hold genuinely different
                // tools, so the prefer-local win below is a real override, not an artefact.
                let! viaPath = (Command.create toolName).OutputStringAsync()

                match viaPath with
                | Ok output -> Assert.That(output.Stdout, Does.Contain "FROM-PATH")
                | Error error -> Assert.Fail $"expected the PATH tool to launch, got {error}"

                // With prefer-local, the local directory wins even though the name is also on PATH.
                let! viaLocal = (Command.create toolName |> Command.preferLocal localDir).OutputStringAsync()

                match viaLocal with
                | Ok output ->
                    Assert.That(output.Stdout, Does.Contain "FROM-LOCAL")
                    Assert.That(output.Stdout, Does.Not.Contain "FROM-PATH", "prefer-local must win over PATH")
                | Error error -> Assert.Fail $"expected the prefer-local tool to launch, got {error}"
            finally
                Environment.SetEnvironmentVariable("PATH", originalPath)

                if not isWindows then
                    NativeEnv.setenv ("PATH", restorePath, 1) |> ignore

                Directory.Delete(localDir, true)
                Directory.Delete(pathDir, true)
        }
        :> Task

    [<Test>]
    member this.``PreferLocal searches multiple directories in the order added (T-182)``() : Task =
        task {
            let dir1 = freshDir "order-1"
            let dir2 = freshDir "order-2"
            let toolName = "pk182-order-tool"

            try
                this.WriteMarkerTool(dir1, toolName, "DIR-ONE") |> ignore
                this.WriteMarkerTool(dir2, toolName, "DIR-TWO") |> ignore

                // dir1 added first -> dir1 wins.
                let! firstWins =
                    (Command.create toolName |> Command.preferLocal dir1 |> Command.preferLocal dir2)
                        .OutputStringAsync()

                match firstWins with
                | Ok output -> Assert.That(output.Stdout, Does.Contain "DIR-ONE")
                | Error error -> Assert.Fail $"expected dir1 (added first) to win, got {error}"

                // Reversed order -> dir2 wins, proving order IS priority, not mere set membership.
                let! secondWins =
                    (Command.create toolName |> Command.preferLocal dir2 |> Command.preferLocal dir1)
                        .OutputStringAsync()

                match secondWins with
                | Ok output -> Assert.That(output.Stdout, Does.Contain "DIR-TWO")
                | Error error -> Assert.Fail $"expected dir2 (added first) to win, got {error}"
            finally
                Directory.Delete(dir1, true)
                Directory.Delete(dir2, true)
        }
        :> Task

    [<Test>]
    member this.``PreferLocal resolves a relative directory against CurrentDir and substitutes an absolute path (T-182)``
        ()
        : Task =
        task {
            let baseDir = freshDir "relbase"
            let toolsDir = Path.Combine(baseDir, "tools")
            let toolName = "pk182-rel-tool"

            try
                this.WriteMarkerTool(toolsDir, toolName, "REL-HIT") |> ignore

                // A RELATIVE prefer-local dir ("tools") with CurrentDir set to baseDir: it must resolve
                // against baseDir (where the child runs), not the parent's current directory, and the OS
                // must be handed the resolved ABSOLUTE path (argv0 / %~f0 rooted under baseDir/tools). The
                // tool lives under a random temp path, unreachable from the parent's cwd, so it can ONLY
                // resolve via the CurrentDir anchoring.
                let command =
                    Command.create toolName
                    |> Command.currentDir baseDir
                    |> Command.preferLocal "tools"

                let! result = command.OutputStringAsync()

                match result with
                | Error error -> Assert.Fail $"expected the relative prefer-local tool to launch, got {error}"
                | Ok output ->
                    Assert.That(output.Code, Is.EqualTo(Some 0))
                    Assert.That(output.Stdout, Does.Contain "REL-HIT")

                    let argv0 =
                        output.Stdout.Split('\n')
                        |> Array.tryFind (fun l -> l.StartsWith "ARGV0=")
                        |> Option.map (fun l -> l.Substring("ARGV0=".Length).Trim())

                    match argv0 with
                    | Some path ->
                        let rootedMessage = $"the substituted program path must be absolute, got '{path}'"

                        Assert.That(Path.IsPathRooted path, Is.True, rootedMessage)

                        let expectedPrefix = Path.GetFullPath toolsDir

                        let comparison =
                            if isWindows then
                                StringComparison.OrdinalIgnoreCase
                            else
                                StringComparison.Ordinal

                        let underMessage =
                            $"the program path '{path}' must be rooted under the CurrentDir-anchored '{expectedPrefix}'"

                        Assert.That(path.StartsWith(expectedPrefix, comparison), Is.True, underMessage)
                    | None -> Assert.Fail $"the tool did not report its argv0; stdout was: {output.Stdout}"
            finally
                Directory.Delete(baseDir, true)
        }
        :> Task

    [<Test>]
    member this.``PreferLocal honours probeDir semantics: Windows PATHEXT / POSIX executable bit (T-182)``() : Task =
        task {
            let dir = freshDir "probe"
            let toolName = "pk182-probe-tool"

            try
                if isWindows then
                    // A `.cmd` shim in the prefer-local directory with NO `.exe` sibling: `probeDir` appends
                    // the PATHEXT extension to find it, and the launch routes it through `cmd.exe /d /c`
                    // exactly as it would on PATH — the same shared probe, not a second copy.
                    let cmdPath = Path.Combine(dir, toolName + ".cmd")
                    File.WriteAllText(cmdPath, "@echo off\r\necho PROBE-CMD\r\n")

                    let! result = (Command.create toolName |> Command.preferLocal dir).OutputStringAsync()

                    match result with
                    | Error error -> Assert.Fail $"expected the prefer-local .cmd shim to launch, got {error}"
                    | Ok output ->
                        Assert.That(output.Code, Is.EqualTo(Some 0))
                        Assert.That(output.Stdout, Does.Contain "PROBE-CMD")
                else
                    // A file WITHOUT an executable bit must be skipped (`probeDir` requires one on POSIX), so
                    // the bare name does not resolve prefer-local and falls through to a NotFound; adding the
                    // bit makes the very same file resolve and launch.
                    let toolPath = Path.Combine(dir, toolName)
                    File.WriteAllText(toolPath, "#!/bin/sh\necho PROBE-EXEC\n")
                    File.SetUnixFileMode(toolPath, UnixFileMode.UserRead ||| UnixFileMode.UserWrite)

                    let! notExecutable = (Command.create toolName |> Command.preferLocal dir).OutputStringAsync()

                    match notExecutable with
                    | Error error ->
                        Assert.That(
                            error.IsNotFound,
                            Is.True,
                            $"a non-executable file must not resolve prefer-local, got {error}"
                        )
                    | Ok output ->
                        Assert.Fail
                            $"expected NotFound for a non-executable prefer-local file, but it ran: {output.Stdout}"

                    File.SetUnixFileMode(
                        toolPath,
                        UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute
                    )

                    let! executable = (Command.create toolName |> Command.preferLocal dir).OutputStringAsync()

                    match executable with
                    | Error error -> Assert.Fail $"expected the now-executable prefer-local tool to launch, got {error}"
                    | Ok output -> Assert.That(output.Stdout, Does.Contain "PROBE-EXEC")
            finally
                Directory.Delete(dir, true)
        }
        :> Task

    [<Test>]
    member this.``Command.preferLocal mirrors the instance PreferLocal, launching the same tool (T-182)``() : Task =
        task {
            let dir = freshDir "parity"
            let toolName = "pk182-parity-tool"

            try
                this.WriteMarkerTool(dir, toolName, "PARITY-OK") |> ignore

                let build (applyPreferLocal: Command -> Command) =
                    Command.create toolName |> applyPreferLocal

                let! viaModule = (build (Command.preferLocal dir)).OutputStringAsync()
                let! viaInstance = (build (fun c -> c.PreferLocal dir)).OutputStringAsync()

                match viaModule, viaInstance with
                | Ok m, Ok i ->
                    Assert.That(m.Stdout, Does.Contain "PARITY-OK")

                    Assert.That(
                        i.Stdout,
                        Is.EqualTo m.Stdout,
                        "Command.preferLocal must produce output identical to the instance .PreferLocal"
                    )
                | _ ->
                    Assert.Fail
                        $"expected both prefer-local paths to launch, got module={viaModule}, instance={viaInstance}"
            finally
                Directory.Delete(dir, true)
        }
        :> Task

    // ---- Command.ResolveProgram / CliClient.ResolveProgram (T-183): resolve against the EFFECTIVE CHILD
    // PATH ------------------------------------------------------------------------------------------------
    //
    // These preflight the program a command WOULD launch, against its effective child PATH (its
    // Env/EnvRemove/EnvClear plus PreferLocal) — the same resolver the real spawn uses — WITHOUT spawning.
    // They deliberately do NOT touch the process environment (only `Command.Env`), so no native `setenv`
    // (K-064) is needed: nothing here relies on the real OS bare-name search seeing an augmented PATH.

    /// Whether `path`, made absolute, sits under `dir` (also made absolute) — case-insensitively on
    /// Windows, case-sensitively on POSIX, matching how the resolver compares paths.
    member private _.IsUnder(dir: string, path: string) : bool =
        let comparison =
            if isWindows then
                StringComparison.OrdinalIgnoreCase
            else
                StringComparison.Ordinal

        Path.GetFullPath(path).StartsWith(Path.GetFullPath dir, comparison)

    [<Test>]
    member _.``relative path-form ResolveProgram and spawn both anchor to CurrentDir``() : Task =
        task {
            let dir = freshDir "current-dir-path"
            let baseName = "processkit-relative-tool"

            try
                let relativeProgram, expectedPath, args =
                    if isWindows then
                        let toolPath = Path.Combine(dir, baseName + ".exe")

                        let comSpec =
                            match Environment.GetEnvironmentVariable "ComSpec" with
                            | null -> failwith "ComSpec is required for the Windows relative-path test"
                            | value -> value

                        File.Copy(comSpec, toolPath)
                        ".\\" + baseName + ".exe", toolPath, [ "/d"; "/c"; "echo CURRENT-DIR-HIT" ]
                    else
                        let toolPath = Path.Combine(dir, baseName)
                        File.WriteAllText(toolPath, "#!/bin/sh\necho CURRENT-DIR-HIT\n")
                        File.SetUnixFileMode(toolPath, UnixFileMode.UserRead ||| UnixFileMode.UserExecute)
                        "./" + baseName, toolPath, []

                let command =
                    Command.create relativeProgram |> Command.args args |> Command.currentDir dir

                match command.ResolveProgram() with
                | Error error -> Assert.Fail $"CurrentDir-relative preflight failed: {error}"
                | Ok resolved -> Assert.That(resolved, Is.EqualTo(Path.GetFullPath expectedPath))

                match! command.OutputStringAsync() with
                | Error error -> Assert.Fail $"CurrentDir-relative spawn failed: {error}"
                | Ok result -> Assert.That(result.Stdout, Does.Contain "CURRENT-DIR-HIT")
            finally
                Directory.Delete(dir, true)
        }
        :> Task

    [<Test>]
    member this.``ResolveProgram resolves against the command's Env PATH override, where Exec.which does not (T-183)``
        ()
        =
        let dir = freshDir "env-path"
        let toolName = "pk183-env-tool"

        try
            this.WriteMarkerTool(dir, toolName, "ENV-HIT") |> ignore

            // The tool lives only in `dir`, which is NOT on the process PATH — so the process-scoped
            // `Exec.which` cannot find it...
            match Exec.which toolName with
            | Ok resolved ->
                Assert.Fail $"expected '{toolName}' to be absent from the process PATH, but which found '{resolved}'"
            | Error error -> Assert.That(error.IsNotFound, Is.True)

            // ...but the command carries `Env "PATH" dir`, so ResolveProgram — against the effective child
            // PATH — resolves it. This which-vs-ResolveProgram distinction is the crux of T-183.
            let command = Command.create toolName |> Command.env "PATH" dir

            match command.ResolveProgram() with
            | Error error -> Assert.Fail $"expected ResolveProgram to resolve via the Env PATH override, got {error}"
            | Ok resolved ->
                Assert.That(File.Exists resolved, Is.True)

                Assert.That(
                    this.IsUnder(dir, resolved),
                    Is.True,
                    $"'{resolved}' must be under the override dir '{dir}'"
                )
        finally
            Directory.Delete(dir, true)

    [<Test>]
    member this.``ResolveProgram honours EnvClear plus a fresh Env PATH (T-183)``() =
        let dir = freshDir "envclear"
        let toolName = "pk183-envclear-tool"

        try
            this.WriteMarkerTool(dir, toolName, "CLEAR-HIT") |> ignore

            // EnvClear wipes the inherited environment (PATH and, on Windows, PATHEXT); a fresh Env "PATH"
            // then supplies the only PATH the child — and ResolveProgram — will see. On Windows the resolver
            // falls back to the default PATHEXT set when PATHEXT is absent, so a `.cmd` shim still resolves.
            let command = Command.create toolName |> Command.envClear |> Command.env "PATH" dir

            match command.ResolveProgram() with
            | Error error -> Assert.Fail $"expected ResolveProgram to resolve under EnvClear + fresh PATH, got {error}"
            | Ok resolved ->
                Assert.That(File.Exists resolved, Is.True)
                Assert.That(this.IsUnder(dir, resolved), Is.True, $"'{resolved}' must be under '{dir}'")
        finally
            Directory.Delete(dir, true)

    [<Test>]
    member this.``ResolveProgram consults PreferLocal before the effective PATH (T-183)``() =
        let localDir = freshDir "rp-local"
        let pathDir = freshDir "rp-path"
        let toolName = "pk183-prio-tool"

        try
            this.WriteMarkerTool(localDir, toolName, "FROM-LOCAL") |> ignore
            this.WriteMarkerTool(pathDir, toolName, "FROM-PATH") |> ignore

            // The same bare name is reachable via BOTH the effective PATH (`Env "PATH" pathDir`) and a
            // prefer-local directory (`localDir`). Prefer-local is consulted first, so it wins — the order
            // the real launch uses, verified here without spawning.
            let command =
                Command.create toolName
                |> Command.env "PATH" pathDir
                |> Command.preferLocal localDir

            match command.ResolveProgram() with
            | Error error -> Assert.Fail $"expected ResolveProgram to resolve prefer-local, got {error}"
            | Ok resolved ->
                Assert.That(this.IsUnder(localDir, resolved), Is.True, $"prefer-local must win: '{resolved}'")

                Assert.That(
                    this.IsUnder(pathDir, resolved),
                    Is.False,
                    "prefer-local must be consulted before the effective PATH"
                )
        finally
            Directory.Delete(localDir, true)
            Directory.Delete(pathDir, true)

    [<Test>]
    member _.``ResolveProgram and a real spawn agree on NotFound/Searched for the same command config (T-183)``
        ()
        : Task =
        task {
            let dir = freshDir "miss"

            try
                // A genuinely-absent program, with an `Env "PATH"` override naming an (empty) directory.
                // Both the no-spawn ResolveProgram and a real spawn of the SAME command must fail NotFound,
                // and — since both derive the diagnostic from the one shared effective-PATH resolver — must
                // report the identical `Searched`. No native `setenv` is needed: a changed child PATH miss is
                // refused by that resolver before `posix_spawnp` can walk the process environment.
                let command = Command.create missingProgram |> Command.env "PATH" dir

                let resolveResult = command.ResolveProgram()
                let! spawnResult = command.OutputStringAsync()

                match resolveResult, spawnResult with
                | Error resolveError, Error spawnError ->
                    Assert.That(
                        resolveError.IsNotFound,
                        Is.True,
                        $"ResolveProgram should be NotFound, got {resolveError}"
                    )

                    Assert.That(spawnError.IsNotFound, Is.True, $"spawn should be NotFound, got {spawnError}")

                    match resolveError, spawnError with
                    | ProcessError.NotFound(resolveProgram, resolveSearched),
                      ProcessError.NotFound(spawnProgram, spawnSearched) ->
                        Assert.That(spawnProgram, Is.EqualTo resolveProgram)
                        Assert.That(spawnSearched, Is.EqualTo resolveSearched)
                        // And the `Searched` names the command's EFFECTIVE (overridden) PATH — `dir` — not
                        // the process's own PATH, the whole point of a command-level resolve.
                        Assert.That(resolveSearched, Is.EqualTo(Some dir))
                    | _ -> Assert.Fail "expected both errors to be NotFound"
                | other -> Assert.Fail $"expected both ResolveProgram and spawn to fail NotFound, got {other}"
            finally
                Directory.Delete(dir, true)
        }
        :> Task

    [<Test>]
    member this.``CliClient.ResolveProgram mirrors Command.ResolveProgram for the template command (T-183)``() =
        let dir = freshDir "cli"
        let toolName = "pk183-cli-tool"

        try
            this.WriteMarkerTool(dir, toolName, "CLI-HIT") |> ignore

            let viaCommand =
                (Command.create toolName |> Command.env "PATH" dir).ResolveProgram()

            // The client's template carries the same `Env "PATH"` default, so its ResolveProgram resolves
            // the client's own program the same way — the parity `CliClient.EnsureAvailableAsync` has with
            // `Exec.which`, but for the effective-child-PATH resolver.
            let client = (CliClient.create toolName).WithDefaults(fun t -> t.Env("PATH", dir))

            let viaClient = client.ResolveProgram()

            match viaCommand, viaClient with
            | Ok fromCommand, Ok fromClient ->
                Assert.That(this.IsUnder(dir, fromClient), Is.True, $"'{fromClient}' must be under '{dir}'")

                Assert.That(
                    fromClient,
                    Is.EqualTo fromCommand,
                    "CliClient.ResolveProgram must match Command.ResolveProgram for the template command"
                )
            | _ -> Assert.Fail $"expected both to resolve, got command={viaCommand}, client={viaClient}"
        finally
            Directory.Delete(dir, true)

/// The POSIX **security** helpers are deliberately the one thing in this library that is NOT resolved
/// like every other program (T-317): `setpriv` (the `Command.Uid`/`Gid`/`Groups` drop and
/// `Command.KillOnParentDeath`'s `--pdeathsig` arming) and `setsid --ctty` (`Command.Pty`'s controlling
/// terminal) are looked up ONLY in a fixed list of trusted system directories and pinned by absolute
/// path, never searched for on the calling process's `PATH`. The reason is the same one that makes
/// `Native.Windows` take `cmd.exe` from `Environment.SystemDirectory` instead of `PATH`/`%ComSpec%` — a
/// helper that can be hijacked defeats the hardening it implements — except that the POSIX case is the
/// more privileged of the two: `setpriv` runs as root, before the credentials it exists to drop.
///
/// These tests pin BOTH halves of that contract: a helper reachable on `PATH` but absent from every
/// trusted directory produces an honest typed refusal and is never executed, and a helper present in a
/// trusted directory is run by its absolute path from there. The `setpriv` paths are driven through
/// `Command.KillOnParentDeath`, the one knob that reaches the helper without needing root.
///
/// Sequential (no `[<Parallelizable>]`, as for `WhichResolutionTests` above): these tests mutate the
/// process `PATH` (managed and native) and a process-wide test seam, both restored in a `finally`.
[<TestFixture>]
type TrustedHelperResolutionTests() =

    let isLinux = RuntimeInformation.IsOSPlatform OSPlatform.Linux

    let freshDir (tag: string) =
        let dir =
            Path.Combine(Path.GetTempPath(), "processkit-helper-" + tag + "-" + Guid.NewGuid().ToString "N")

        Directory.CreateDirectory dir |> ignore
        dir

    /// Write an executable POSIX stand-in for a util-linux security helper into `dir`. It records the
    /// path it was actually invoked as (`$0`) into `markerPath`, skips its own option flags, and `exec`s
    /// whatever target follows — a faithful enough impostor that a resolution which DID pick it up would
    /// succeed loudly (leaving the marker behind) instead of failing in some ambiguous way. Returns the
    /// shim's full path.
    let writeHelperShim (dir: string) (helper: string) (markerPath: string) =
        let path = Path.Combine(dir, helper)

        let script =
            String.concat
                "\n"
                [ "#!/bin/sh"
                  "printf '%s' \"$0\" > \"" + markerPath + "\""
                  "while [ $# -gt 0 ]; do"
                  "  case \"$1\" in"
                  "    --*) shift ;;"
                  "    *) break ;;"
                  "  esac"
                  "done"
                  "exec \"$@\""
                  "" ]

        File.WriteAllText(path, script)

        File.SetUnixFileMode(path, UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute)

        path

    /// The process `PATH`, never null — the native `setenv` restore takes a non-nullable string, unlike
    /// managed `SetEnvironmentVariable`.
    let currentPath () =
        match Environment.GetEnvironmentVariable "PATH" with
        | null -> ""
        | value -> value

    /// Set the process `PATH` in BOTH the managed view (which `Exec.which` reads) and the native
    /// `environ` that the real `posix_spawnp` `PATH` search reads — the managed/native split documented
    /// on `NativeEnv` at the top of this file. POSIX-only, as these tests are.
    let setPath (value: string) =
        Environment.SetEnvironmentVariable("PATH", value)
        NativeEnv.setenv ("PATH", value, 1) |> ignore

    let assertMissingSetpriv (label: string) (expectedProgram: string) (result: Result<'a, ProcessError>) =
        match result with
        | Error(ProcessError.Spawn(program, reason)) ->
            Assert.That(program, Is.EqualTo expectedProgram, label)
            Assert.That(reason, Does.Contain "setpriv", label)
        | Error other -> Assert.Fail $"{label}: expected ProcessError.Spawn for the missing setpriv, got {other}"
        | Ok _ -> Assert.Fail $"{label}: expected the missing setpriv to refuse the launch"

    [<Test>]
    member _.``setpriv is loaded only from a trusted system directory, never from PATH (T-317)``() : Task =
        task {
            if not isLinux then
                Assert.Ignore
                    "Linux-only: KillOnParentDeath is the setpriv path reachable without root, and macOS/BSD refuse it before any helper resolution."

            let pathDir = freshDir "setpriv-on-path"
            let trustedDir = freshDir "setpriv-trusted-empty"
            let marker = Path.Combine(pathDir, "ran.marker")
            let shimPath = writeHelperShim pathDir "setpriv" marker
            let originalPath = currentPath ()
            let callerProgram: string = "/bin/sh"

            try
                setPath (pathDir + string Path.PathSeparator + originalPath)

                // Premise of the test: a `setpriv` really IS reachable on the PATH the OS itself would
                // search — this is exactly the planted-helper situation the pinning exists to defeat, not
                // a host that simply lacks the tool.
                let plantedFirst: string =
                    "the planted setpriv must be the first PATH match, or this test proves nothing"

                match Exec.which "setpriv" with
                | Ok resolved -> Assert.That(resolved, Is.EqualTo shimPath, plantedFirst)
                | Error error -> Assert.Fail $"expected the planted setpriv to be on PATH, got {error}"

                // ... and no TRUSTED directory holds one.
                Native.Posix.trustedHelperDirectoriesForTests <- Some [ trustedDir ]

                let command =
                    Command.create callerProgram
                    |> Command.args [ "-c"; "exit 0" ]
                    |> Command.killOnParentDeath

                match! command.OutputStringAsync() with
                | Ok result ->
                    Assert.Fail
                        $"the spawn must refuse when no trusted directory holds setpriv, but it ran (outcome {result.Outcome})"
                | Error(ProcessError.Spawn(program, reason)) ->
                    // The refusal names the CALLER's program and the knob that needed the helper — the
                    // same honest diagnostic a host genuinely missing util-linux already produced.
                    let namesCaller: string =
                        "the refusal must name the caller's own program, not the helper"

                    Assert.That(program, Is.EqualTo callerProgram, namesCaller)
                    Assert.That(reason, Does.Contain "setpriv")
                    Assert.That(reason, Does.Contain "KillOnParentDeath")

                    let namesSearched: string =
                        "the refusal must name the trusted directories actually searched"

                    Assert.That(reason, Does.Contain trustedDir, namesSearched)
                | Error other ->
                    Assert.Fail $"expected a typed ProcessError.Spawn naming the missing setpriv, got {other}"

                let neverRan: string =
                    "a setpriv sitting on PATH must never be executed - that is the hijack this pinning prevents"

                Assert.That(File.Exists marker, Is.False, neverRan)
            finally
                Native.Posix.trustedHelperDirectoriesForTests <- None
                setPath originalPath
                Directory.Delete(pathDir, true)
                Directory.Delete(trustedDir, true)
        }
        :> Task

    [<Test>]
    member _.``rlimit helper composition preserves the requested program in spawn errors``() : Task =
        task {
            if not isLinux then
                Assert.Ignore "Linux-only: setpriv and the cgroup launch path are Linux mechanisms."

            if not (File.Exists "/dev/ptmx") then
                Assert.Ignore "This host has no POSIX pseudo-terminal device for the PTY launch path."

            let trustedDir = freshDir "rlimit-diagnostic-program"
            let prlimitMarker = Path.Combine(trustedDir, "prlimit.marker")
            let setsidMarker = Path.Combine(trustedDir, "setsid.marker")
            let prlimitPath = writeHelperShim trustedDir "prlimit" prlimitMarker
            let setsidPath = writeHelperShim trustedDir "setsid" setsidMarker
            let callerProgram = "/bin/sh"

            try
                Native.Posix.trustedHelperDirectoriesForTests <- Some [ trustedDir ]

                Assert.That(
                    Native.Posix.trustedHelperPathForTests "prlimit",
                    Is.EqualTo(Some prlimitPath),
                    "the rlimit rewrite must resolve before the missing setpriv is diagnosed"
                )

                Assert.That(
                    Native.Posix.trustedHelperPathForTests "setsid",
                    Is.EqualTo(Some setsidPath),
                    "the PTY path must pass its controlling-terminal helper gate"
                )

                Assert.That(
                    Native.Posix.trustedHelperPathForTests "setpriv",
                    Is.EqualTo(None: string option),
                    "setpriv must be deterministically unavailable in the trusted directories"
                )

                let limitedDrop =
                    Command.create callerProgram
                    |> Command.args [ "-c"; "exit 0" ]
                    |> Command.rlimit RlimitResource.NoFile 64L 128L
                    |> Command.killOnParentDeath

                let! ordinary = limitedDrop.OutputStringAsync()
                assertMissingSetpriv "ordinary spawn" callerProgram ordinary

                let! pty = (limitedDrop |> Command.pty).OutputStringAsync()
                assertMissingSetpriv "PTY spawn" callerProgram pty

                let cgroupProcs = Path.Combine(trustedDir, "cgroup.procs")

                Native.Posix.spawnPosixIntoCgroup limitedDrop cgroupProcs
                |> assertMissingSetpriv "cgroup spawn" callerProgram

                let limitedDetachedDrop =
                    Command.create callerProgram
                    |> Command.args [ "-c"; "exit 0" ]
                    |> Command.rlimit RlimitResource.NoFile 64L 128L
                    |> Command.uid (NativeEnv.geteuid ())

                Native.Posix.spawnDetachedPosix limitedDetachedDrop
                |> assertMissingSetpriv "detached spawn" callerProgram

                let groupsWithoutDrop =
                    Command.create callerProgram
                    |> Command.args [ "-c"; "exit 0" ]
                    |> Command.rlimit RlimitResource.NoFile 64L 128L
                    |> Command.groups [ NativeEnv.geteuid () ]
                    |> Command.pty

                match! groupsWithoutDrop.OutputStringAsync() with
                | Error(ProcessError.Spawn(program, reason)) ->
                    Assert.That(program, Is.EqualTo callerProgram, "supplementary-groups refusal")
                    Assert.That(reason, Does.Contain "Command.Groups")
                | Error other ->
                    Assert.Fail $"expected ProcessError.Spawn for supplementary groups without a drop, got {other}"
                | Ok _ -> Assert.Fail "supplementary groups without a Uid/Gid drop must be refused"

                Assert.That(File.Exists prlimitMarker, Is.False, "the pre-spawn refusal must not execute prlimit")
                Assert.That(File.Exists setsidMarker, Is.False, "the pre-spawn refusal must not execute setsid")
            finally
                Native.Posix.trustedHelperDirectoriesForTests <- None
                Directory.Delete(trustedDir, true)
        }
        :> Task

    [<Test>]
    member _.``setpriv found in a trusted directory runs from there, by absolute path (T-317)``() : Task =
        task {
            if not isLinux then
                Assert.Ignore
                    "Linux-only: KillOnParentDeath is the setpriv path reachable without root, and macOS/BSD refuse it before any helper resolution."

            let trustedDir = freshDir "setpriv-trusted-hit"
            let marker = Path.Combine(trustedDir, "ran.marker")
            let shimPath = writeHelperShim trustedDir "setpriv" marker
            let expectedStdout: string = "RAN"

            // The PATH is deliberately left ALONE here: it still holds the host's real
            // `/usr/bin/setpriv`, so a resolution that fell back to `PATH` would run that one and never
            // touch this shim. The marker is therefore positive proof that the trusted list — not
            // `PATH` — decided, and its contents prove the helper was invoked by its absolute path.
            try
                Native.Posix.trustedHelperDirectoriesForTests <- Some [ trustedDir ]

                let command =
                    Command.create "/bin/sh"
                    |> Command.args [ "-c"; "printf RAN" ]
                    |> Command.killOnParentDeath

                match! command.OutputStringAsync() with
                | Error error -> Assert.Fail $"the trusted-directory setpriv must be used, got {error}"
                | Ok result ->
                    // The shim `exec`s the target in place, so the target's own output and exit code are
                    // what the run reports — the same in-place composition the real helper provides.
                    Assert.That(result.Stdout, Is.EqualTo expectedStdout)
                    Assert.That(result.Code, Is.EqualTo(Some 0))

                    let ranFromTrusted: string =
                        "the helper in the trusted directory must be the one that ran"

                    Assert.That(File.Exists marker, Is.True, ranFromTrusted)

                    let pinnedAbsolute: string =
                        "the helper must be launched by the absolute path resolved from the trusted directory"

                    Assert.That(File.ReadAllText marker, Is.EqualTo shimPath, pinnedAbsolute)
            finally
                Native.Posix.trustedHelperDirectoriesForTests <- None
                Directory.Delete(trustedDir, true)
        }
        :> Task

    [<Test>]
    member _.``the setsid ctty helper is loaded only from a trusted directory, never from PATH (T-317)``() : Task =
        task {
            if not isLinux then
                Assert.Ignore
                    "Linux-only: the pty ctty helper path needs a Linux host with /dev/ptmx; macOS/BSD have no such helper at all."

            let pathDir = freshDir "setsid-on-path"
            let trustedDir = freshDir "setsid-trusted-empty"
            let marker = Path.Combine(pathDir, "ran.marker")
            writeHelperShim pathDir "setsid" marker |> ignore
            let originalPath = currentPath ()

            try
                setPath (pathDir + string Path.PathSeparator + originalPath)
                Native.Posix.trustedHelperDirectoriesForTests <- Some [ trustedDir ]

                let command =
                    Command.create "/bin/sh" |> Command.args [ "-c"; "exit 0" ] |> Command.pty

                match! command.OutputStringAsync() with
                | Ok result ->
                    Assert.Fail
                        $"a PTY must be Unsupported when no trusted directory holds setsid, but it ran (outcome {result.Outcome})"
                | Error(ProcessError.Unsupported reason) ->
                    // The same honest capability answer a host genuinely without the helper gets — never a
                    // socketpair pretending to be a tty, and never a `setsid` picked up off PATH.
                    Assert.That(reason, Does.Contain "Pty")
                    Assert.That(reason, Does.Contain "setsid")

                    let namesSearched: string =
                        "the refusal must name the trusted directories actually searched"

                    Assert.That(reason, Does.Contain trustedDir, namesSearched)
                | Error other -> Assert.Fail $"expected a typed ProcessError.Unsupported for the PTY, got {other}"

                let neverRan: string =
                    "a setsid sitting on PATH must never become the controlling-terminal helper"

                Assert.That(File.Exists marker, Is.False, neverRan)
            finally
                Native.Posix.trustedHelperDirectoriesForTests <- None
                setPath originalPath
                Directory.Delete(pathDir, true)
                Directory.Delete(trustedDir, true)
        }
        :> Task

/// Which of two same-named executables a Windows launch actually starts when the command supplies its own
/// child `PATH` (T-339). `CreateProcessW` with `lpApplicationName = NULL` resolves the image in the
/// PARENT's context — it walks the CURRENT PROCESS's `PATH`, never the environment block it is handed for
/// the child — so leaving a bare `.exe` name to that search would launch a parent-`PATH` namesake instead
/// of the match `Command.ResolveProgram` promised, or fail while a namesake sits on the child's own
/// `PATH`. Every raw Windows launch path is covered — the ordinary spawn, ConPTY, and the fire-and-forget
/// detached launch — because each calls `CreateProcessW`/`CreateProcessAsUserW` itself.
///
/// The two planted images are chosen so the winner is unmistakable: a copy of `cmd.exe` in the child's
/// `PATH` (the one that must run — only it can exit with the requested code) and a copy of `whoami.exe` in
/// the process's own `PATH` (the impostor — it refuses `cmd`'s arguments with its own usage error and can
/// never produce that code). Both are real, launchable PE images, which a hand-written stub could not be.
///
/// Sequential (no `[<Parallelizable>]`, as for the fixtures above): every test temporarily prepends a
/// directory to the process `PATH` and restores it in a `finally`. Windows-only — this is a
/// `CreateProcessW` contract; the POSIX launch resolves a bare name through `posix_spawnp` against the
/// native `environ` (see the `NativeEnv` note at the top of this file).
[<TestFixture>]
type WindowsChildPathLaunchTests() =

    let isWindows = RuntimeInformation.IsOSPlatform OSPlatform.Windows

    /// The exit code only the planted `cmd.exe` copy can produce for `childHitArgs`.
    let childHitCode = 7

    let childHitArgs = [ "/d"; "/c"; "exit 7" ]

    /// Printed by the planted `cmd.exe` copy when a run cannot report an exit code back (the detached
    /// launch), so a redirected stdout file says which image ran.
    let childHitMarker = "PK339-CHILD-PATH-HIT"

    let freshDir (tag: string) =
        let dir =
            Path.Combine(Path.GetTempPath(), "processkit-t339-" + tag + "-" + Guid.NewGuid().ToString "N")

        Directory.CreateDirectory dir |> ignore
        dir

    let deleteDirQuietly (dir: string) =
        try
            Directory.Delete(dir, true)
        with
        | :? IOException
        | :? UnauthorizedAccessException ->
            // Windows can still hold a just-exited child's image file open for a moment. A leftover temp
            // directory is harmless and must never fail the assertion under test.
            ()

    let deleteFileQuietly (path: string) =
        try
            File.Delete path
        with
        | :? IOException
        | :? UnauthorizedAccessException ->
            // Same rationale as `deleteDirQuietly`: best-effort cleanup of a temp file only.
            ()

    /// Copy a real system executable into `dir` under `name`.exe, and return its full path — normalized
    /// the same way the resolver normalizes a match, so the two are directly comparable.
    let plant (dir: string) (name: string) (systemExe: string) =
        let target = Path.Combine(dir, name + ".exe")
        File.Copy(Path.Combine(Environment.SystemDirectory, systemExe), target)
        Path.GetFullPath target

    let processPath () =
        match Environment.GetEnvironmentVariable "PATH" with
        | null -> ""
        | value -> value

    /// The file's current contents, or `""` while it does not exist yet / is momentarily unreadable.
    /// Share-compatible so a read never trips a sharing violation against a detached child's write handle.
    let readIfPresent (path: string) =
        try
            use stream =
                new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)

            use reader = new StreamReader(stream)
            reader.ReadToEnd()
        with
        | :? FileNotFoundException
        | :? IOException ->
            // The child has not created (or not yet flushed) the file — an expected step of the poll
            // below, not a failure; the caller simply tries again until its deadline.
            ""

    let waitUntil (timeout: TimeSpan) (predicate: unit -> bool) =
        let deadline = DateTime.UtcNow + timeout
        let mutable ok = predicate ()

        while not ok && DateTime.UtcNow < deadline do
            Thread.Sleep 50
            ok <- predicate ()

        ok

    /// Kill a detached child: nothing in the library owns one, so a test that leaves it running leaks a
    /// process past the whole run.
    let killQuietly (pid: int) =
        try
            use proc = System.Diagnostics.Process.GetProcessById pid
            proc.Kill()
        with _ ->
            // Best-effort test cleanup: the child may already have exited (`ArgumentException`) or be
            // exiting concurrently; either way there is nothing left to kill, and a cleanup failure must
            // never fail the assertion under test.
            ()

    /// Plant the pair — the `cmd.exe` copy in a directory only the CHILD's `PATH` will name, and the
    /// `whoami.exe` impostor in a directory prepended to the PROCESS's own `PATH` — then run
    /// `body childDir childTool`. The process `PATH` and both directories are restored/removed afterwards.
    member private _.WithPlantedPair(toolName: string, body: string -> string -> Task) : Task =
        task {
            let parentDir = freshDir "parent"
            let childDir = freshDir "child"
            let originalPath = processPath ()

            try
                plant parentDir toolName "whoami.exe" |> ignore
                let childTool = plant childDir toolName "cmd.exe"

                Environment.SetEnvironmentVariable("PATH", parentDir + string Path.PathSeparator + originalPath)

                do! body childDir childTool
            finally
                Environment.SetEnvironmentVariable("PATH", originalPath)
                deleteDirQuietly parentDir
                deleteDirQuietly childDir
        }

    [<Test>]
    member this.``Windows: an ordinary spawn launches the child-PATH match, not the parent-PATH namesake (T-339)``
        ()
        : Task =
        task {
            if not isWindows then
                Assert.Ignore "Windows-only: CreateProcessW resolves a bare name in the parent's context."
            else
                let toolName = "pk339-ordinary"

                do!
                    this.WithPlantedPair(
                        toolName,
                        fun childDir childTool ->
                            task {
                                let command =
                                    Command.create toolName
                                    |> Command.args childHitArgs
                                    |> Command.env "PATH" childDir
                                    |> Command.timeout (TimeSpan.FromSeconds 30.0)

                                match command.ResolveProgram() with
                                | Error error -> Assert.Fail $"the child-PATH match must preflight, got {error}"
                                | Ok resolved -> Assert.That(resolved, Is.EqualTo(childTool).IgnoreCase)

                                match! command.OutputStringAsync() with
                                | Error error -> Assert.Fail $"the child-PATH match must launch, got {error}"
                                | Ok result ->
                                    let launchedChildMatch: string =
                                        "an ordinary spawn must launch the executable on the child's own PATH, not the same-named one on the process PATH"

                                    Assert.That(
                                        result.Outcome,
                                        Is.EqualTo(Outcome.Exited childHitCode),
                                        launchedChildMatch
                                    )
                            }
                            :> Task
                    )
        }
        :> Task

    [<Test>]
    member this.``Windows: a ConPTY spawn launches the child-PATH match, not the parent-PATH namesake (T-339)``
        ()
        : Task =
        task {
            if not isWindows then
                Assert.Ignore "Windows-only: ConPTY is the Windows PTY launch path."
            else
                let toolName = "pk339-conpty"

                do!
                    this.WithPlantedPair(
                        toolName,
                        fun childDir childTool ->
                            task {
                                let command =
                                    Command.create toolName
                                    |> Command.args childHitArgs
                                    |> Command.env "PATH" childDir
                                    |> Command.pty
                                    |> Command.timeout (TimeSpan.FromSeconds 30.0)

                                match command.ResolveProgram() with
                                | Error error -> Assert.Fail $"the child-PATH match must preflight, got {error}"
                                | Ok resolved -> Assert.That(resolved, Is.EqualTo(childTool).IgnoreCase)

                                match! command.OutputStringAsync() with
                                | Error(ProcessError.Unsupported message) when message.Contains "1809" ->
                                    // Pre-1809 host without ConPTY — the documented typed-Unsupported path.
                                    Assert.Ignore $"host lacks ConPTY: {message}"
                                | Error error -> Assert.Fail $"the child-PATH match must launch, got {error}"
                                | Ok result ->
                                    let launchedChildMatch: string =
                                        "a ConPTY spawn must launch the executable on the child's own PATH, not the same-named one on the process PATH"

                                    Assert.That(
                                        result.Outcome,
                                        Is.EqualTo(Outcome.Exited childHitCode),
                                        launchedChildMatch
                                    )
                            }
                            :> Task
                    )
        }
        :> Task

    [<Test>]
    member this.``Windows: a detached launch starts the child-PATH match, not the parent-PATH namesake (T-339)``
        ()
        : Task =
        task {
            if not isWindows then
                Assert.Ignore "Windows-only: the detached launch is its own CreateProcessW call site."
            else
                let toolName = "pk339-detached"

                let marker =
                    Path.Combine(Path.GetTempPath(), $"pk339-detached-{Guid.NewGuid():N}.log")

                try
                    do!
                        this.WithPlantedPair(
                            toolName,
                            fun childDir childTool ->
                                task {
                                    // A detached child reports no exit code to anyone, so the proof is what
                                    // it writes: only the `cmd.exe` copy echoes the marker; the `whoami.exe`
                                    // impostor answers these arguments with a usage error instead.
                                    let command =
                                        Command.create toolName
                                        |> Command.args [ "/d"; "/c"; "echo " + childHitMarker ]
                                        |> Command.env "PATH" childDir
                                        |> Command.stdoutToFile marker false

                                    match command.ResolveProgram() with
                                    | Error error -> Assert.Fail $"the child-PATH match must preflight, got {error}"
                                    | Ok resolved -> Assert.That(resolved, Is.EqualTo(childTool).IgnoreCase)

                                    match command.LaunchDetached() with
                                    | Error error -> Assert.Fail $"the child-PATH match must launch, got {error}"
                                    | Ok detached ->
                                        try
                                            let ran =
                                                waitUntil (TimeSpan.FromSeconds 30.0) (fun () ->
                                                    (readIfPresent marker).Contains childHitMarker)

                                            let launchedChildMatch: string =
                                                "a detached launch must start the executable on the child's own PATH, not the same-named one on the process PATH"

                                            Assert.That(ran, Is.True, launchedChildMatch)
                                        finally
                                            killQuietly detached.Pid
                                }
                                :> Task
                        )
                finally
                    deleteFileQuietly marker
        }
        :> Task

    [<Test>]
    member _.``Windows: a child-PATH miss is NotFound before any spawn, on every launch path (T-339)``() : Task =
        task {
            if not isWindows then
                Assert.Ignore "Windows-only: CreateProcessW resolves a bare name in the parent's context."
            else
                let toolName = "pk339-miss"
                // The impostor is a `cmd.exe` copy this time: it CAN write the marker, so a leaked launch
                // leaves positive evidence rather than merely failing differently.
                let parentDir = freshDir "miss-parent"
                let childDir = freshDir "miss-child"

                let marker = Path.Combine(Path.GetTempPath(), $"pk339-miss-{Guid.NewGuid():N}.log")

                let originalPath = processPath ()

                try
                    plant parentDir toolName "cmd.exe" |> ignore

                    Environment.SetEnvironmentVariable("PATH", parentDir + string Path.PathSeparator + originalPath)

                    // The child's PATH names an empty directory, so the program the caller asked for is
                    // genuinely absent from it — while a same-named, runnable executable sits on the
                    // process's own PATH, which is exactly what the OS's own search would find. No
                    // `Timeout`: a detached launch refuses that knob, and this one command is launched
                    // through all three paths so they answer the identical configuration.
                    let command =
                        Command.create toolName
                        |> Command.args [ "/d"; "/c"; "echo PK339-LEAKED" ]
                        |> Command.env "PATH" childDir

                    let expectedSearched =
                        match command.ResolveProgram() with
                        | Ok resolved -> failwith $"the child PATH must not resolve '{toolName}', got '{resolved}'"
                        | Error(ProcessError.NotFound(_, searched)) ->
                            Assert.That(searched, Is.EqualTo(Some childDir))
                            searched
                        | Error other -> failwith $"expected NotFound from the preflight, got {other}"

                    // Every launch path must fail with the identical diagnostic the preflight just gave,
                    // rather than launching the parent-PATH namesake.
                    let assertRefused (label: string) (error: ProcessError) =
                        match error with
                        | ProcessError.NotFound(program, searched) ->
                            Assert.That(program, Is.EqualTo toolName, label)
                            Assert.That(searched, Is.EqualTo expectedSearched, label)
                        | other -> Assert.Fail $"{label}: expected NotFound, got {other}"

                    match! command.OutputStringAsync() with
                    | Ok result ->
                        Assert.Fail
                            $"an ordinary spawn launched a parent-PATH namesake the child's PATH never held (outcome {result.Outcome})"
                    | Error error -> assertRefused "ordinary spawn" error

                    match! (command |> Command.pty).OutputStringAsync() with
                    | Error(ProcessError.Unsupported message) when message.Contains "1809" ->
                        Assert.Ignore $"host lacks ConPTY: {message}"
                    | Ok result ->
                        Assert.Fail
                            $"a ConPTY spawn launched a parent-PATH namesake the child's PATH never held (outcome {result.Outcome})"
                    | Error error -> assertRefused "ConPTY spawn" error

                    match (command |> Command.stdoutToFile marker false).LaunchDetached() with
                    | Ok detached ->
                        killQuietly detached.Pid
                        Assert.Fail "a detached launch started a parent-PATH namesake the child's PATH never held"
                    | Error error -> assertRefused "detached launch" error

                    let neverRan: string =
                        "the same-named executable on the process PATH must never be launched for a command whose own PATH does not hold it"

                    Assert.That((readIfPresent marker).Contains "PK339-LEAKED", Is.False, neverRan)
                finally
                    Environment.SetEnvironmentVariable("PATH", originalPath)
                    deleteFileQuietly marker
                    deleteDirQuietly parentDir
                    deleteDirQuietly childDir
        }
        :> Task

    [<Test>]
    member _.``Windows: an unchanged child PATH keeps the OS's own bare-name search (T-339)``() : Task =
        task {
            if not isWindows then
                Assert.Ignore "Windows-only: this is the CreateProcessW bare-name search."
            else
                let toolName = "pk339-unchanged"
                let dir = freshDir "unchanged"
                let originalPath = processPath ()

                try
                    let tool = plant dir toolName "cmd.exe"
                    Environment.SetEnvironmentVariable("PATH", dir + string Path.PathSeparator + originalPath)

                    // No `Env "PATH"`: the child inherits the process's own PATH, so the OS's search and the
                    // library's resolution walk the identical value. The launch is left to the OS exactly as
                    // it was before the effective-child-PATH substitution existed.
                    let command =
                        Command.create toolName
                        |> Command.args childHitArgs
                        |> Command.timeout (TimeSpan.FromSeconds 30.0)

                    match command.ResolveProgram() with
                    | Error error -> Assert.Fail $"a PATH-planted tool must preflight, got {error}"
                    | Ok resolved -> Assert.That(resolved, Is.EqualTo(Path.GetFullPath tool).IgnoreCase)

                    match! command.OutputStringAsync() with
                    | Error error -> Assert.Fail $"a PATH-planted tool must still launch unchanged, got {error}"
                    | Ok result ->
                        let stillLaunches: string =
                            "a command that leaves the child's PATH alone must still launch through the OS's own bare-name search"

                        Assert.That(result.Outcome, Is.EqualTo(Outcome.Exited childHitCode), stillLaunches)
                finally
                    Environment.SetEnvironmentVariable("PATH", originalPath)
                    deleteDirQuietly dir
        }
        :> Task

/// POSIX counterpart of the child-PATH launch contract. libc's `posix_spawnp` searches the launching
/// process's native environment rather than the `envp` supplied for the child, so the pair below makes a
/// wrong lookup observable: the process PATH names a parent marker while the command's effective child
/// PATH names a different executable. Direct ordinary and detached launches must pin the latter, and a
/// child-PATH miss must be refused before the parent namesake can run.
[<TestFixture>]
type PosixChildPathLaunchTests() =

    let isWindows = RuntimeInformation.IsOSPlatform OSPlatform.Windows

    let freshDir (tag: string) =
        let dir =
            Path.Combine(Path.GetTempPath(), "processkit-posix-child-path-" + tag + "-" + Guid.NewGuid().ToString "N")

        Directory.CreateDirectory dir |> ignore
        dir

    let deleteDirQuietly (dir: string) =
        try
            if Directory.Exists dir then
                Directory.Delete(dir, true)
        with
        | :? IOException
        | :? UnauthorizedAccessException ->
            // Best-effort cleanup of test-only temporary files; a locked file is unrelated to the launch
            // contract already asserted by the test.
            ()

    let deleteFileQuietly (path: string) =
        try
            if File.Exists path then
                File.Delete path
        with
        | :? IOException
        | :? UnauthorizedAccessException ->
            // Best-effort cleanup of a detached child's output file after its behavior was already asserted.
            ()

    let currentPath () =
        match Environment.GetEnvironmentVariable "PATH" with
        | null -> ""
        | value -> value

    let setPath (value: string) =
        Environment.SetEnvironmentVariable("PATH", value)
        NativeEnv.setenv ("PATH", value, 1) |> ignore

    let unsetPath () =
        Environment.SetEnvironmentVariable("PATH", null)
        NativeEnv.unsetenv "PATH" |> ignore

    let restorePath (value: string option) =
        match value with
        | Some path -> setPath path
        | None -> unsetPath ()

    let writeTool (dir: string) (name: string) (marker: string) =
        let path = Path.Combine(dir, name)

        File.WriteAllText(path, "#!/bin/sh\nprintf '%s' '" + marker + "'\n")

        File.SetUnixFileMode(path, UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute)
        Path.GetFullPath path

    let readIfPresent (path: string) =
        try
            use stream =
                new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)

            use reader = new StreamReader(stream)
            reader.ReadToEnd()
        with
        | :? FileNotFoundException
        | :? IOException ->
            // A detached child has not created or flushed the file yet; polling until the deadline is the
            // expected synchronization path.
            ""

    let waitUntil (timeout: TimeSpan) (predicate: unit -> bool) =
        let deadline = DateTime.UtcNow + timeout
        let mutable matched = predicate ()

        while not matched && DateTime.UtcNow < deadline do
            Thread.Sleep 50
            matched <- predicate ()

        matched

    let killQuietly (pid: int) =
        try
            use childProcess = System.Diagnostics.Process.GetProcessById pid
            childProcess.Kill()
        with _ ->
            // The detached script normally exits before cleanup reaches it; an already-gone process needs
            // no further teardown, and cleanup must not obscure the launch assertion.
            ()

    let assertRefusedBeforeSpawn (toolName: string) (marker: string) (command: Command) : Task =
        task {
            let expectedSearched =
                match command.ResolveProgram() with
                | Ok resolved -> failwith $"the effective empty PATH must not resolve '{toolName}', got '{resolved}'"
                | Error(ProcessError.NotFound(_, searched)) -> searched
                | Error other -> failwith $"expected NotFound from preflight, got {other}"

            let assertRefused (label: string) (error: ProcessError) =
                match error with
                | ProcessError.NotFound(program, searched) ->
                    Assert.That(program, Is.EqualTo toolName, label)
                    Assert.That(searched, Is.EqualTo expectedSearched, label)
                | other -> Assert.Fail $"{label}: expected NotFound, got {other}"

            match! command.OutputStringAsync() with
            | Ok result -> Assert.Fail $"ordinary spawn executed '{toolName}' (outcome {result.Outcome})"
            | Error error -> assertRefused "ordinary spawn" error

            Assert.That(File.Exists marker, Is.False, "ordinary pre-spawn refusal must leave no marker")

            match (command |> Command.stdoutToFile marker false).LaunchDetached() with
            | Ok detached ->
                killQuietly detached.Pid
                Assert.Fail $"detached spawn executed '{toolName}'"
            | Error error -> assertRefused "detached spawn" error

            Assert.That(File.Exists marker, Is.False, "detached pre-spawn refusal must leave no marker")
        }

    member private _.WithPlantedPair(toolName: string, body: string -> string -> Task) : Task =
        task {
            let parentDir = freshDir "parent"
            let childDir = freshDir "child"
            let originalPath = currentPath ()

            try
                writeTool parentDir toolName "PARENT-PATH" |> ignore
                let childTool = writeTool childDir toolName "CHILD-PATH"
                setPath (parentDir + string Path.PathSeparator + originalPath)
                do! body childDir childTool
            finally
                setPath originalPath
                deleteDirQuietly parentDir
                deleteDirQuietly childDir
        }

    [<Test>]
    member this.``POSIX ordinary spawn launches the effective child-PATH match``() : Task =
        task {
            if isWindows then
                Assert.Ignore "POSIX-only: this exercises posix_spawnp's parent-environment search."
            else
                do!
                    this.WithPlantedPair(
                        "processkit-child-path-ordinary",
                        fun childDir childTool ->
                            task {
                                let command =
                                    Command.create "processkit-child-path-ordinary"
                                    |> Command.env "PATH" childDir
                                    |> Command.timeout (TimeSpan.FromSeconds 30.0)

                                match command.ResolveProgram() with
                                | Error error -> Assert.Fail $"the child-PATH executable must preflight, got {error}"
                                | Ok resolved -> Assert.That(resolved, Is.EqualTo childTool)

                                match! command.OutputStringAsync() with
                                | Error error -> Assert.Fail $"the child-PATH executable must launch, got {error}"
                                | Ok result -> Assert.That(result.Stdout, Is.EqualTo "CHILD-PATH")
                            }
                            :> Task
                    )
        }
        :> Task

    [<Test>]
    member this.``POSIX detached spawn launches the effective child-PATH match``() : Task =
        task {
            if isWindows then
                Assert.Ignore "POSIX-only: this exercises the detached posix_spawnp call site."
            else
                let marker =
                    Path.Combine(Path.GetTempPath(), $"processkit-detached-child-path-{Guid.NewGuid():N}.log")

                try
                    do!
                        this.WithPlantedPair(
                            "processkit-child-path-detached",
                            fun childDir childTool ->
                                task {
                                    let command =
                                        Command.create "processkit-child-path-detached"
                                        |> Command.env "PATH" childDir
                                        |> Command.stdoutToFile marker false

                                    match command.ResolveProgram() with
                                    | Error error ->
                                        Assert.Fail $"the detached child-PATH executable must preflight, got {error}"
                                    | Ok resolved -> Assert.That(resolved, Is.EqualTo childTool)

                                    match command.LaunchDetached() with
                                    | Error error ->
                                        Assert.Fail $"the detached child-PATH executable must launch, got {error}"
                                    | Ok detached ->
                                        try
                                            let ranChild =
                                                waitUntil (TimeSpan.FromSeconds 30.0) (fun () ->
                                                    (readIfPresent marker).Contains "CHILD-PATH")

                                            Assert.That(
                                                ranChild,
                                                Is.True,
                                                "the detached launch must run the child-PATH image, not the parent-PATH namesake"
                                            )
                                        finally
                                            killQuietly detached.Pid
                                }
                                :> Task
                        )
                finally
                    deleteFileQuietly marker
        }
        :> Task

    [<Test>]
    member _.``POSIX EnvClear miss is NotFound before ordinary or detached spawn``() : Task =
        task {
            if isWindows then
                Assert.Ignore "POSIX-only: this guards both direct posix_spawnp call sites."
            else
                let toolName = "processkit-child-path-miss"
                let parentDir = freshDir "miss-parent"

                let marker =
                    Path.Combine(Path.GetTempPath(), $"processkit-child-path-miss-{Guid.NewGuid():N}.log")

                let originalPath = currentPath ()

                try
                    writeTool parentDir toolName "PARENT-LEAK" |> ignore
                    setPath (parentDir + string Path.PathSeparator + originalPath)

                    let command = Command.create toolName |> Command.envClear

                    let expectedSearched =
                        match command.ResolveProgram() with
                        | Ok resolved -> failwith $"an EnvClear command must not resolve '{toolName}', got '{resolved}'"
                        | Error(ProcessError.NotFound(_, searched)) -> searched
                        | Error other -> failwith $"expected NotFound from preflight, got {other}"

                    let assertRefused (label: string) (error: ProcessError) =
                        match error with
                        | ProcessError.NotFound(program, searched) ->
                            Assert.That(program, Is.EqualTo toolName, label)
                            Assert.That(searched, Is.EqualTo expectedSearched, label)
                        | other -> Assert.Fail $"{label}: expected NotFound, got {other}"

                    match! command.OutputStringAsync() with
                    | Ok result -> Assert.Fail $"ordinary spawn ran the parent-PATH namesake (outcome {result.Outcome})"
                    | Error error -> assertRefused "ordinary spawn" error

                    match (command |> Command.stdoutToFile marker false).LaunchDetached() with
                    | Ok detached ->
                        killQuietly detached.Pid
                        Assert.Fail "detached spawn ran the parent-PATH namesake"
                    | Error error -> assertRefused "detached spawn" error

                    Assert.That(
                        (readIfPresent marker).Contains "PARENT-LEAK",
                        Is.False,
                        "the parent-PATH namesake must never execute"
                    )
                finally
                    setPath originalPath
                    deleteFileQuietly marker
                    deleteDirQuietly parentDir
        }
        :> Task

    [<Test>]
    member _.``POSIX absent process PATH cannot enable libc default-path launch``() : Task =
        task {
            if isWindows then
                Assert.Ignore "POSIX-only: this guards libc's absent-PATH default search."
            else
                let originalPath = Environment.GetEnvironmentVariable "PATH" |> Option.ofObj

                try
                    unsetPath ()

                    for label, configure in
                        [ "inherited", id
                          "EnvClear", Command.envClear
                          "EnvRemove", Command.envRemove "PATH" ] do
                        let marker =
                            Path.Combine(Path.GetTempPath(), $"processkit-absent-path-{label}-{Guid.NewGuid():N}.log")

                        try
                            let shellScript = $"printf 'LIBC-DEFAULT-PATH' > '{marker}'"

                            let command = Command.create "sh" |> Command.args [ "-c"; shellScript ] |> configure

                            do! assertRefusedBeforeSpawn "sh" marker command
                        finally
                            deleteFileQuietly marker
                finally
                    restorePath originalPath
        }
        :> Task

    [<Test>]
    member _.``POSIX empty process PATH cannot enable current-directory launch``() : Task =
        task {
            if isWindows then
                Assert.Ignore "POSIX-only: this guards libc's empty-PATH current-directory search."
            else
                let originalPath = Environment.GetEnvironmentVariable "PATH" |> Option.ofObj
                let originalDirectory = Directory.GetCurrentDirectory()
                let dir = freshDir "empty-current-dir"

                try
                    Directory.SetCurrentDirectory dir
                    setPath ""

                    for label, configure in
                        [ "inherited", id
                          "EnvClear", Command.envClear
                          "EnvRemove", Command.envRemove "PATH"
                          "EnvOverride", Command.env "PATH" "" ] do
                        let toolName = $"processkit-empty-path-{label.ToLowerInvariant()}"
                        let marker = Path.Combine(dir, $"{toolName}.log")

                        try
                            writeTool dir toolName "CURRENT-DIRECTORY-FALLBACK" |> ignore
                            let command = Command.create toolName |> configure
                            do! assertRefusedBeforeSpawn toolName marker command
                        finally
                            deleteFileQuietly marker
                finally
                    Directory.SetCurrentDirectory originalDirectory
                    restorePath originalPath
                    deleteDirQuietly dir
        }
        :> Task

    [<Test>]
    member _.``POSIX inherited empty PATH components keep preflight and native spawn on the current directory``
        ()
        : Task =
        task {
            if isWindows then
                Assert.Ignore "POSIX-only: this exercises an inherited empty PATH component through posix_spawnp."
            else
                let originalPath = Environment.GetEnvironmentVariable "PATH" |> Option.ofObj
                let originalDirectory = Directory.GetCurrentDirectory()
                let currentDir = freshDir "inherited-empty-current"
                let laterDir = freshDir "inherited-empty-later"
                let toolName = "processkit-inherited-empty-path"

                try
                    writeTool currentDir toolName "CURRENT-DIRECTORY" |> ignore
                    writeTool laterDir toolName "LATER-DIRECTORY" |> ignore
                    Directory.SetCurrentDirectory currentDir
                    // macOS canonicalizes /var to /private/var here; compare with the cwd spelling the resolver sees.
                    let expectedCurrentTool = Path.Combine(Directory.GetCurrentDirectory(), toolName)

                    for label, path in
                        [ "leading", string Path.PathSeparator + laterDir
                          "trailing", "missing" + string Path.PathSeparator ] do
                        // These launches deliberately leave PATH inherited, so keep the managed resolver
                        // and libc's native posix_spawnp search on the same empty-component value.
                        setPath path

                        match Exec.which toolName with
                        | Error error ->
                            Assert.Fail $"{label}: Exec.which must resolve the empty component, got {error}"
                        | Ok resolved -> Assert.That(resolved, Is.EqualTo expectedCurrentTool, label)

                        let command = Command.create toolName |> Command.timeout (TimeSpan.FromSeconds 30.0)

                        match command.ResolveProgram() with
                        | Error error ->
                            Assert.Fail
                                $"{label}: Command.ResolveProgram must resolve the current-directory tool, got {error}"
                        | Ok resolved -> Assert.That(resolved, Is.EqualTo expectedCurrentTool, label)

                        match! command.OutputStringAsync() with
                        | Error error ->
                            Assert.Fail $"{label}: the inherited empty-component command must launch, got {error}"
                        | Ok result -> Assert.That(result.Stdout, Is.EqualTo "CURRENT-DIRECTORY", label)
                finally
                    Directory.SetCurrentDirectory originalDirectory
                    restorePath originalPath
                    deleteDirQuietly currentDir
                    deleteDirQuietly laterDir
        }
        :> Task

    [<Test>]
    member _.``POSIX child PATH preserves empty-component order against CurrentDir and relative entries``() : Task =
        task {
            if isWindows then
                Assert.Ignore "POSIX-only: Windows deliberately keeps its fixed-directory search unchanged."
            else
                let baseDir = freshDir "empty-component-order"
                let laterDir = Path.Combine(baseDir, "later")
                let toolName = "processkit-empty-component-order"

                try
                    Directory.CreateDirectory laterDir |> ignore
                    let currentTool = writeTool baseDir toolName "CURRENT-DIRECTORY"
                    writeTool laterDir toolName "LATER-DIRECTORY" |> ignore

                    for label, path, expectedPath, expectedMarker in
                        [ "leading", ":later", currentTool, "CURRENT-DIRECTORY"
                          "trailing", "missing:", currentTool, "CURRENT-DIRECTORY"
                          "middle", "missing::later", currentTool, "CURRENT-DIRECTORY" ] do
                        let command =
                            Command.create toolName
                            |> Command.currentDir baseDir
                            |> Command.env "PATH" path
                            |> Command.timeout (TimeSpan.FromSeconds 30.0)

                        match command.ResolveProgram() with
                        | Error error -> Assert.Fail $"{label}: expected PATH resolution, got {error}"
                        | Ok resolved -> Assert.That(resolved, Is.EqualTo expectedPath, label)

                        match! command.OutputStringAsync() with
                        | Error error -> Assert.Fail $"{label}: the resolved command must launch, got {error}"
                        | Ok result -> Assert.That(result.Stdout, Is.EqualTo expectedMarker, label)
                finally
                    deleteDirQuietly baseDir
        }
        :> Task

    [<Test>]
    member _.``POSIX unchanged child PATH keeps native bare-name launch``() : Task =
        task {
            if isWindows then
                Assert.Ignore "POSIX-only: this preserves the native posix_spawnp search."
            else
                let toolName = "processkit-unchanged-path"
                let dir = freshDir "unchanged"
                let originalPath = currentPath ()

                try
                    let tool = writeTool dir toolName "UNCHANGED-PATH"
                    setPath (dir + string Path.PathSeparator + originalPath)
                    let command = Command.create toolName |> Command.timeout (TimeSpan.FromSeconds 30.0)

                    match command.ResolveProgram() with
                    | Error error -> Assert.Fail $"the process-PATH tool must preflight, got {error}"
                    | Ok resolved -> Assert.That(resolved, Is.EqualTo tool)

                    match! command.OutputStringAsync() with
                    | Error error -> Assert.Fail $"the unchanged-PATH command must launch, got {error}"
                    | Ok result -> Assert.That(result.Stdout, Is.EqualTo "UNCHANGED-PATH")
                finally
                    setPath originalPath
                    deleteDirQuietly dir
        }
        :> Task

    member private _.ExpectedNotFound(command: Command) =
        match command.ResolveProgram() with
        | Error(ProcessError.NotFound(program, searched)) -> program, searched
        | Error other -> failwith $"expected NotFound from preflight, got {other}"
        | Ok resolved -> failwith $"expected preflight refusal, got '{resolved}'"

    member private _.AssertMatchingNotFound
        (label: string, (expectedProgram: string, expectedSearched: string option), error: ProcessError)
        =
        match error with
        | ProcessError.NotFound(program, searched) ->
            Assert.That(program, Is.EqualTo expectedProgram, label)
            Assert.That(searched, Is.EqualTo expectedSearched, label)
        | other -> Assert.Fail $"{label}: expected NotFound, got {other}"

    member private this.AssertCgroupSeamRefused(label: string, marker: string, command: Command) : Task =
        task {
            let procs =
                Path.Combine(Path.GetTempPath(), $"processkit-cgroup-path-{Guid.NewGuid():N}.procs")

            let expected = this.ExpectedNotFound command

            try
                match Native.Posix.spawnPosixIntoCgroup command procs with
                | Error error -> this.AssertMatchingNotFound(label, expected, error)
                | Ok spawned ->
                    let! outcome = Native.Posix.waitPosix spawned.Handle
                    spawned.Stdout |> Option.iter _.Dispose()
                    spawned.Stderr |> Option.iter _.Dispose()
                    spawned.Stdin |> Option.iter _.Dispose()
                    Assert.Fail $"{label}: the cgroup launcher ran despite the preflight miss ({outcome})"

                Assert.That(File.Exists procs, Is.False, $"{label}: the cgroup launcher must not start")
                Assert.That(File.Exists marker, Is.False, $"{label}: the refused image must not run")
            finally
                deleteFileQuietly procs
                deleteFileQuietly marker
        }

    member private this.AssertGroupRefused
        (group: ProcessGroup, label: string, marker: string, command: Command)
        : Task =
        task {
            let expected = this.ExpectedNotFound command

            match! group.StartAsync command with
            | Error error -> this.AssertMatchingNotFound(label, expected, error)
            | Ok running ->
                use running = running
                let! outcome = running.WaitAsync()
                Assert.Fail $"{label}: the real cgroup backend ran despite the preflight miss ({outcome})"

            Assert.That(File.Exists marker, Is.False, $"{label}: the refused image must not run")
        }

    [<Test>]
    member this.``Linux cgroup launcher preflights absent and empty PATH before its shell can search``() : Task =
        task {
            if not (RuntimeInformation.IsOSPlatform OSPlatform.Linux) then
                Assert.Ignore "Linux-only: the cgroup migration launcher is a Linux backend."
            else
                let currentDir = freshDir "cgroup-empty-current"
                let currentTool = "processkit-cgroup-current-namesake"
                let currentMarker = Path.Combine(currentDir, "current.marker")
                let defaultClearMarker = Path.Combine(currentDir, "default-clear.marker")
                let defaultRemoveMarker = Path.Combine(currentDir, "default-remove.marker")

                try
                    File.WriteAllText(
                        Path.Combine(currentDir, currentTool),
                        $"#!/bin/sh\nprintf current > '{currentMarker}'\n"
                    )

                    File.SetUnixFileMode(
                        Path.Combine(currentDir, currentTool),
                        UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute
                    )

                    do!
                        Command.create "sh"
                        |> Command.args [ "-c"; $"printf default > '{defaultClearMarker}'" ]
                        |> Command.envClear
                        |> Command.stdout StdioMode.Null
                        |> Command.stderr StdioMode.Null
                        |> fun command ->
                            this.AssertCgroupSeamRefused("EnvClear/default-path", defaultClearMarker, command)

                    do!
                        Command.create "sh"
                        |> Command.args [ "-c"; $"printf default > '{defaultRemoveMarker}'" ]
                        |> Command.envRemove "PATH"
                        |> Command.stdout StdioMode.Null
                        |> Command.stderr StdioMode.Null
                        |> fun command ->
                            this.AssertCgroupSeamRefused("EnvRemove/default-path", defaultRemoveMarker, command)

                    do!
                        Command.create currentTool
                        |> Command.currentDir currentDir
                        |> Command.env "PATH" ""
                        |> Command.stdout StdioMode.Null
                        |> Command.stderr StdioMode.Null
                        |> fun command ->
                            this.AssertCgroupSeamRefused("empty-PATH/current-directory", currentMarker, command)
                finally
                    deleteDirQuietly currentDir
        }
        :> Task

    [<Test>]
    member this.``Linux cgroup backend uses the same effective child PATH boundary as preflight``() : Task =
        task {
            if not (RuntimeInformation.IsOSPlatform OSPlatform.Linux) then
                Assert.Ignore "Linux-only: this requires the cgroup-v2 backend."
            else
                let options =
                    ProcessGroupOptions().WithMemoryMax(256L * 1024L * 1024L).WithMaxProcesses(64)

                match ProcessGroup.Create options with
                | Error(ProcessError.ResourceLimit _) ->
                    Assert.Ignore "cgroup v2 limits not enforceable here (no delegated writable hierarchy)"
                | Error other -> Assert.Fail $"creating the cgroup-v2 group failed: {other}"
                | Ok group ->
                    use group = group
                    Assert.That(group.Mechanism, Is.EqualTo Mechanism.CgroupV2)

                    let root = freshDir "real-cgroup"
                    let parentDir = Path.Combine(root, "parent")
                    let childDir = Path.Combine(root, "child")
                    let currentDir = Path.Combine(root, "current")
                    Directory.CreateDirectory parentDir |> ignore
                    Directory.CreateDirectory childDir |> ignore
                    Directory.CreateDirectory currentDir |> ignore

                    let originalPath = currentPath ()
                    let pairedName = "processkit-real-cgroup-pair"
                    let currentName = "processkit-real-cgroup-current"
                    let currentMarker = Path.Combine(root, "current.marker")
                    let clearMarker = Path.Combine(root, "clear.marker")
                    let removeMarker = Path.Combine(root, "remove.marker")

                    try
                        writeTool parentDir pairedName "PARENT-CGROUP" |> ignore
                        let childTool = writeTool childDir pairedName "CHILD-CGROUP"
                        writeTool currentDir currentName "CURRENT-CGROUP" |> ignore
                        setPath (parentDir + string Path.PathSeparator + originalPath)

                        let selected =
                            Command.create pairedName
                            |> Command.env "PATH" childDir
                            |> Command.timeout (TimeSpan.FromSeconds 30.0)

                        match selected.ResolveProgram() with
                        | Error error -> Assert.Fail $"the child-PATH image must preflight, got {error}"
                        | Ok resolved -> Assert.That(resolved, Is.EqualTo childTool)

                        match! group.StartAsync selected with
                        | Error error -> Assert.Fail $"the child-PATH image failed through cgroup-v2: {error}"
                        | Ok running ->
                            use running = running

                            match! running.OutputStringAsync() with
                            | Error error -> Assert.Fail $"the cgroup-v2 child failed: {error}"
                            | Ok result -> Assert.That(result.Stdout, Is.EqualTo "CHILD-CGROUP")

                        do!
                            Command.create "sh"
                            |> Command.args [ "-c"; $"printf default > '{clearMarker}'" ]
                            |> Command.envClear
                            |> Command.stdout StdioMode.Null
                            |> Command.stderr StdioMode.Null
                            |> fun command ->
                                this.AssertGroupRefused(
                                    group,
                                    "real cgroup EnvClear/default-path",
                                    clearMarker,
                                    command
                                )

                        do!
                            Command.create "sh"
                            |> Command.args [ "-c"; $"printf default > '{removeMarker}'" ]
                            |> Command.envRemove "PATH"
                            |> Command.stdout StdioMode.Null
                            |> Command.stderr StdioMode.Null
                            |> fun command ->
                                this.AssertGroupRefused(
                                    group,
                                    "real cgroup EnvRemove/default-path",
                                    removeMarker,
                                    command
                                )

                        do!
                            Command.create currentName
                            |> Command.currentDir currentDir
                            |> Command.env "PATH" ""
                            |> Command.stdout StdioMode.Null
                            |> Command.stderr StdioMode.Null
                            |> fun command ->
                                this.AssertGroupRefused(
                                    group,
                                    "real cgroup empty-PATH/current-directory",
                                    currentMarker,
                                    command
                                )
                    finally
                        setPath originalPath
                        deleteDirQuietly root
        }
        :> Task
