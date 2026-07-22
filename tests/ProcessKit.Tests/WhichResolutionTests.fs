namespace ProcessKit.Tests

open System
open System.IO
open System.Runtime.InteropServices
open System.Threading
open System.Threading.Tasks
open NUnit.Framework
open ProcessKit
open ProcessKit.Native

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
                    Common.probeDir dir program |> ignore
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

            try
                this.WriteMarkerTool(localDir, toolName, "FROM-LOCAL") |> ignore
                this.WriteMarkerTool(pathDir, toolName, "FROM-PATH") |> ignore
                Environment.SetEnvironmentVariable("PATH", pathDir + string Path.PathSeparator + originalPath)

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
