namespace ProcessKit.Tests

open System
open System.IO
open System.Runtime.InteropServices
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
