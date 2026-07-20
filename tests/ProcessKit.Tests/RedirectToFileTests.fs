namespace ProcessKit.Tests

open System
open System.Diagnostics
open System.IO
open System.Runtime.InteropServices
open System.Threading
open System.Threading.Tasks
open NUnit.Framework
open ProcessKit

/// Tests for `Command.StdoutToFile` / `Command.StderrToFile` (T-171): a direct, OS-level redirect of the
/// child's stdout/stderr straight to a file, handed to the child as its std handle/fd on the spawn
/// (Windows: an inheritable file handle in `STARTUPINFO`; POSIX: a file fd via a `posix_spawn` file
/// action). There is no parent-side stream and no pump, so the file grows even with the parent draining
/// nothing. Covers the direct-write contract, append vs truncate, coexistence with normal capture of the
/// other stream, the pump-independence guarantee, the builder-boundary conflict rejections, and fd hygiene.
[<TestFixture>]
type RedirectToFileTests() =

    let isWindows = RuntimeInformation.IsOSPlatform OSPlatform.Windows
    let isLinux = RuntimeInformation.IsOSPlatform OSPlatform.Linux
    let runner: IProcessRunner = JobRunner()

    let shell (script: string) =
        if isWindows then
            Command.create "cmd.exe" |> Command.args [ "/c"; script ]
        else
            Command.create "/bin/sh" |> Command.args [ "-c"; script ]

    // A child that writes three known lines to stdout.
    let threeOut =
        if isWindows then
            shell "echo AA&echo BB&echo CC"
        else
            shell "echo AA; echo BB; echo CC"

    let tempFile () =
        Path.Combine(Path.GetTempPath(), $"pk-redirect-{Guid.NewGuid():N}.log")

    // Read a file that MAY still be held open for writing by a live child: open share-compatible
    // (`FileShare.ReadWrite`) so the read never trips a Windows sharing violation against the child's
    // write handle (the redirect opens the file with FILE_SHARE_RW for exactly this).
    let readShared (path: string) : string =
        use fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)

        use reader = new StreamReader(fs)
        reader.ReadToEnd()

    let markers (text: string) =
        text.Split('\n')
        |> Array.map (fun s -> s.Trim())
        |> Array.filter (fun s -> s <> "")
        |> Array.toList

    let deleteQuietly (path: string) =
        try
            File.Delete path
        with _ ->
            // Best-effort test cleanup — a leftover temp file is harmless and must never fail a test.
            ()

    let throwsArg (action: unit -> unit) =
        Assert.Throws<ArgumentException>(Action action) |> ignore

    [<Test>]
    member _.``StdoutToFile writes the child's stdout straight to the file, with nothing captured by the parent``
        ()
        : Task =
        task {
            let path = tempFile ()

            try
                // The parent runs to completion via a buffering verb; because stdout is redirected to the
                // file at the OS level there is NO parent pipe/pump, yet the full output lands in the file.
                match! (threeOut |> Command.stdoutToFile path false).OutputStringAsync() with
                | Error error -> Assert.Fail $"{error}"
                | Ok result ->
                    // Nothing reached the parent — the child's stdout went straight to the file.
                    Assert.That(result.Stdout, Is.Empty, "the parent must capture no stdout for a file redirect")
                    // The file has every line, in order.
                    Assert.That(markers (File.ReadAllText path), Is.EqualTo(box [ "AA"; "BB"; "CC" ]))
            finally
                deleteQuietly path
        }
        :> Task

    [<Test>]
    member _.``StderrToFile redirects stderr to a file while stdout is still captured normally``() : Task =
        task {
            let path = tempFile ()

            let both =
                if isWindows then
                    shell "echo OUT&echo ERR 1>&2"
                else
                    shell "echo OUT; echo ERR >&2"

            try
                match! (both |> Command.stderrToFile path false).OutputStringAsync() with
                | Error error -> Assert.Fail $"{error}"
                | Ok result ->
                    // stdout is captured the ordinary way; stderr went to the file, so the captured
                    // stderr is empty and the file carries it.
                    Assert.That(result.Stdout, Does.Contain "OUT", "stdout must still be captured normally")
                    Assert.That(result.Stderr, Is.Empty, "stderr must not reach the parent under StderrToFile")
                    Assert.That(File.ReadAllText path, Does.Contain "ERR", "the child's stderr must be in the file")
            finally
                deleteQuietly path
        }
        :> Task

    [<Test>]
    member _.``both streams can be redirected to separate files``() : Task =
        task {
            let outPath = tempFile ()
            let errPath = tempFile ()

            let both =
                if isWindows then
                    shell "echo OUT&echo ERR 1>&2"
                else
                    shell "echo OUT; echo ERR >&2"

            try
                let command =
                    both |> Command.stdoutToFile outPath false |> Command.stderrToFile errPath false

                match! command.OutputStringAsync() with
                | Error error -> Assert.Fail $"{error}"
                | Ok result ->
                    Assert.That(result.Stdout, Is.Empty)
                    Assert.That(result.Stderr, Is.Empty)
                    Assert.That(File.ReadAllText outPath, Does.Contain "OUT")
                    Assert.That(File.ReadAllText errPath, Does.Contain "ERR")

                    Assert.That(
                        File.ReadAllText outPath,
                        Does.Not.Contain "ERR",
                        "the streams must land in separate files"
                    )
            finally
                deleteQuietly outPath
                deleteQuietly errPath
        }
        :> Task

    [<Test>]
    member _.``StdoutToFile append preserves existing content; truncate overwrites it``() : Task =
        task {
            let path = tempFile ()

            try
                File.WriteAllText(path, "EXISTING\n")

                // append = true: the child's output is appended after the existing content.
                match! (shell "echo APPENDED" |> Command.stdoutToFile path true).OutputStringAsync() with
                | Error error -> Assert.Fail $"append run failed: {error}"
                | Ok _ ->
                    let appended = File.ReadAllText path
                    Assert.That(appended, Does.Contain "EXISTING", "append must keep the pre-existing content")
                    Assert.That(appended, Does.Contain "APPENDED", "append must add the child's output")

                // append = false: the file is truncated, so only the fresh output remains.
                match! (shell "echo FRESH" |> Command.stdoutToFile path false).OutputStringAsync() with
                | Error error -> Assert.Fail $"truncate run failed: {error}"
                | Ok _ ->
                    let truncated = File.ReadAllText path
                    Assert.That(truncated, Does.Contain "FRESH", "truncate must write the fresh output")
                    Assert.That(truncated, Does.Not.Contain "EXISTING", "truncate must overwrite the old content")
                    Assert.That(truncated, Does.Not.Contain "APPENDED", "truncate must overwrite the appended content")
            finally
                deleteQuietly path
        }
        :> Task

    [<Test>]
    member _.``a redirect file completes with the parent draining no stream (pump independence)``() : Task =
        task {
            let path = tempFile ()

            // Many lines, so the child does real work while the parent deliberately reads NOTHING — no
            // stdout pump exists for a file redirect. The child writes the file directly and exits; the
            // complete file after a drain-free run proves the output never depended on a parent pump.
            let manyLines =
                if isWindows then
                    shell "for /L %i in (1,1,200) do @echo line%i"
                else
                    shell "i=1; while [ $i -le 200 ]; do echo line$i; i=$((i+1)); done"

            try
                match! runner.StartAsync(manyLines |> Command.stdoutToFile path false, CancellationToken.None) with
                | Error error -> Assert.Fail $"{error}"
                | Ok running ->
                    // Only wait for exit — never read a stream.
                    let! outcome = running.WaitAsync()

                    match outcome with
                    | Outcome.Exited 0 ->
                        let lines =
                            File.ReadAllText path |> markers |> List.filter (fun l -> l.StartsWith "line")

                        Assert.That(
                            List.length lines,
                            Is.EqualTo 200,
                            "every line must reach the file without a parent pump"
                        )
                    | other -> Assert.Fail $"unexpected outcome: {other}"

                    do! (running :> IAsyncDisposable).DisposeAsync()
            finally
                deleteQuietly path
        }
        :> Task

    [<Test>]
    member _.``the redirect file grows in stages driven by the child while the parent pumps nothing``() : Task =
        task {
            let path = tempFile ()

            // A child that writes a LARGE first batch straight to the file, then blocks reading stdin, then
            // writes SECOND. The parent gates it purely through stdin (never reading stdout — there is no
            // stdout stream), so observing the first batch in the file before writing to stdin proves the
            // child wrote the file directly, and SECOND appearing only after the write proves the growth is
            // child-driven. The first batch is deliberately ~20 KB (2000 lines) so it overflows a POSIX
            // shell's block buffer for a regular file and is flushed to disk BEFORE the child blocks on
            // `read` — otherwise a fully-buffered shell would hold it until exit and the mid-run check would
            // be racy. cmd.exe writes each `echo` straight through (no such buffering).
            let gated =
                if isWindows then
                    shell "(for /L %i in (1,1,2000) do @echo FIRSTLINE)&set /p x=&echo SECOND"
                    |> Command.keepStdinOpen
                else
                    shell "i=0; while [ $i -lt 2000 ]; do echo FIRSTLINE; i=$((i+1)); done; read x; echo SECOND"
                    |> Command.keepStdinOpen

            try
                match! runner.StartAsync(gated |> Command.stdoutToFile path false, CancellationToken.None) with
                | Error error -> Assert.Fail $"{error}"
                | Ok running ->
                    // Poll (share-compatible, the child holds the file open) until FIRST lands — with a
                    // generous deadline so a slow CI host is fine. No stream is ever read here.
                    let deadline = Stopwatch.StartNew()
                    let mutable seenFirst = false

                    while not seenFirst && deadline.Elapsed < TimeSpan.FromSeconds 15.0 do
                        if File.Exists path && (readShared path).Contains "FIRST" then
                            seenFirst <- true
                        else
                            do! Task.Delay 25

                    Assert.That(seenFirst, Is.True, "the child must write FIRST to the file before it is unblocked")

                    Assert.That(
                        readShared path,
                        Does.Not.Contain "SECOND",
                        "SECOND must not appear until the child is unblocked"
                    )

                    // Unblock the child through stdin; it then writes SECOND and exits.
                    match running.TakeStdin() with
                    | None -> Assert.Fail "expected an interactive stdin handle"
                    | Some stdin ->
                        do! stdin.WriteLineAsync "go"
                        do! stdin.FinishAsync()

                    let! outcome = running.WaitAsync()

                    match outcome with
                    | Outcome.Exited 0 ->
                        let final = File.ReadAllText path
                        Assert.That(final, Does.Contain "FIRST")
                        Assert.That(final, Does.Contain "SECOND", "the second stage must have grown the file")
                    | other -> Assert.Fail $"unexpected outcome: {other}"

                    do! (running :> IAsyncDisposable).DisposeAsync()
            finally
                deleteQuietly path
        }
        :> Task

    // ---- Builder-boundary conflict rejection (bidirectional), and the allowed combinations ----

    [<Test>]
    member _.``StdoutToFile rejects StdoutTee in either chaining order``() =
        use sink = new MemoryStream()

        throwsArg (fun () ->
            (Command.create "git" |> Command.stdoutToFile "log.txt" false).StdoutTee sink
            |> ignore)

        throwsArg (fun () -> (Command.create "git").StdoutTee(sink).StdoutToFile("log.txt", false) |> ignore)

    [<Test>]
    member _.``StdoutToFile rejects OnStdoutLine in either chaining order``() =
        let handler = Action<string>(fun _ -> ())

        throwsArg (fun () ->
            (Command.create "git" |> Command.stdoutToFile "log.txt" false).OnStdoutLine handler
            |> ignore)

        throwsArg (fun () ->
            (Command.create "git").OnStdoutLine(handler).StdoutToFile("log.txt", false)
            |> ignore)

    [<Test>]
    member _.``StderrToFile rejects StderrTee in either chaining order``() =
        use sink = new MemoryStream()

        throwsArg (fun () ->
            (Command.create "git" |> Command.stderrToFile "log.txt" false).StderrTee sink
            |> ignore)

        throwsArg (fun () -> (Command.create "git").StderrTee(sink).StderrToFile("log.txt", false) |> ignore)

    [<Test>]
    member _.``StderrToFile rejects OnStderrLine in either chaining order``() =
        let handler = Action<string>(fun _ -> ())

        throwsArg (fun () ->
            (Command.create "git" |> Command.stderrToFile "log.txt" false).OnStderrLine handler
            |> ignore)

        throwsArg (fun () ->
            (Command.create "git").OnStderrLine(handler).StderrToFile("log.txt", false)
            |> ignore)

    [<Test>]
    member _.``StdoutToFile and StderrToFile each reject MergeStderr in either chaining order``() =
        throwsArg (fun () ->
            (Command.create "git" |> Command.stdoutToFile "o.txt" false).MergeStderr()
            |> ignore)

        throwsArg (fun () ->
            (Command.create "git" |> Command.mergeStderr).StdoutToFile("o.txt", false)
            |> ignore)

        throwsArg (fun () ->
            (Command.create "git" |> Command.stderrToFile "e.txt" false).MergeStderr()
            |> ignore)

        throwsArg (fun () ->
            (Command.create "git" |> Command.mergeStderr).StderrToFile("e.txt", false)
            |> ignore)

    [<Test>]
    member _.``StdoutToFile and StderrToFile each reject Pty in either chaining order``() =
        throwsArg (fun () -> (Command.create "git" |> Command.stdoutToFile "o.txt" false).Pty() |> ignore)
        throwsArg (fun () -> (Command.create "git" |> Command.pty).StdoutToFile("o.txt", false) |> ignore)
        throwsArg (fun () -> (Command.create "git" |> Command.stderrToFile "e.txt" false).Pty() |> ignore)
        throwsArg (fun () -> (Command.create "git" |> Command.pty).StderrToFile("e.txt", false) |> ignore)

    [<Test>]
    member _.``StdoutToFile rejects a null path and an embedded NUL``() =
        Assert.Throws<ArgumentNullException>(
            Action(fun () ->
                Command.create "git"
                |> Command.stdoutToFile (Unchecked.defaultof<string>) false
                |> ignore)
        )
        |> ignore

        throwsArg (fun () -> Command.create "git" |> Command.stdoutToFile "bad\000path" false |> ignore)

    [<Test>]
    member _.``the supported combinations build without throwing``() =
        // stdout to a file while capturing/observing stderr the ordinary way, and vice versa; both streams
        // to files; and a stderr tee alongside a stdout file redirect (different streams — no conflict).
        Assert.DoesNotThrow(
            Action(fun () ->
                use sink = new MemoryStream()

                Command.create "git"
                |> Command.stdoutToFile "o.txt" false
                |> Command.onStderrLine (fun _ -> ())
                |> ignore

                Command.create "git"
                |> Command.stderrToFile "e.txt" true
                |> Command.onStdoutLine (fun _ -> ())
                |> ignore

                (Command.create "git" |> Command.stdoutToFile "o.txt" false).StderrTee sink
                |> ignore

                Command.create "git"
                |> Command.stdoutToFile "o.txt" false
                |> Command.stderrToFile "e.txt" false
                |> ignore)
        )

    [<Test>]
    member _.``a later Stdout mode clears a StdoutToFile redirect (last destination wins)``() : Task =
        task {
            let path = tempFile ()

            try
                File.WriteAllText(path, "UNTOUCHED\n")

                // StdoutToFile then Stdout(Null): the file redirect is cleared, so the child's output is
                // discarded to the null device and the pre-existing file is never opened/truncated.
                let command =
                    shell "echo SHOULD_NOT_APPEAR"
                    |> Command.stdoutToFile path false
                    |> Command.stdout StdioMode.Null

                match! command.OutputStringAsync() with
                | Error error -> Assert.Fail $"{error}"
                | Ok result ->
                    Assert.That(result.Stdout, Is.Empty)

                    Assert.That(
                        File.ReadAllText path,
                        Does.Contain "UNTOUCHED",
                        "Stdout(Null) must have cleared the redirect"
                    )

                    Assert.That(File.ReadAllText path, Does.Not.Contain "SHOULD_NOT_APPEAR")
            finally
                deleteQuietly path
        }
        :> Task

    [<Test>]
    member _.``repeated redirect-to-file spawns do not leak file descriptors``() : Task =
        task {
            if not isLinux then
                Assert.Ignore "Linux-only: counts open descriptors via /proc/self/fd"

            let openFdCount () =
                Directory.GetFileSystemEntries("/proc/self/fd").Length

            let runOnce () : Task =
                task {
                    let path = tempFile ()

                    try
                        let! _ = (threeOut |> Command.stdoutToFile path false).OutputStringAsync()
                        ()
                    finally
                        deleteQuietly path
                }

            // Warm up so one-time fds (SIGCHLD/pidfd reaper, thread-pool eventfds, JIT) are already in the
            // baseline rather than mistaken for a leak.
            do! runOnce ()
            GC.Collect()
            GC.WaitForPendingFinalizers()
            do! Task.Delay 50
            let before = openFdCount ()

            for _ in 1..40 do
                do! runOnce ()

            GC.Collect()
            GC.WaitForPendingFinalizers()
            do! Task.Delay 50
            let after = openFdCount ()

            Assert.That(
                after,
                Is.LessThanOrEqualTo(before + 8),
                $"file descriptors grew from {before} to {after} across 40 redirect spawns — likely an fd leak"
            )
        }
        :> Task
