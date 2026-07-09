namespace ProcessKit.Tests

open System
open System.Collections.Generic
open System.IO
open System.Runtime.InteropServices
open System.Threading
open System.Threading.Tasks
open NUnit.Framework
open ProcessKit

/// Tests for `Command.MergeStderr` (T-053): an OS-level `2>&1` that folds the child's stderr into its
/// stdout at spawn (POSIX `dup2`, Windows shared `STARTUPINFO.hStdError`), so the two streams interleave
/// honestly on the single stdout stream. Covers the buffering verbs, the streaming verbs, a pipeline
/// stage, the builder-boundary rejection of incompatible knobs, and fd hygiene under load.
[<TestFixture>]
type MergeStderrTests() =

    let isWindows = RuntimeInformation.IsOSPlatform OSPlatform.Windows
    let isLinux = RuntimeInformation.IsOSPlatform OSPlatform.Linux
    let runner: IProcessRunner = JobRunner()

    let shell (script: string) =
        if isWindows then
            Command.create "cmd.exe" |> Command.args [ "/c"; script ]
        else
            Command.create "/bin/sh" |> Command.args [ "-c"; script ]

    // A child that writes ALTERNATING lines to stdout and stderr, in the order AA (out), BB (err),
    // CC (out), DD (err). The shell runs the four writes sequentially, so with stderr merged into the
    // one stdout pipe they land in that exact byte order — an interleaving a post-hoc `Combined`
    // (all stdout, then all stderr) can never reproduce.
    let alternating =
        if isWindows then
            shell "echo AA&echo BB 1>&2&echo CC&echo DD 1>&2"
        else
            shell "echo AA; echo BB >&2; echo CC; echo DD >&2"

    let tokenSet = set [ "AA"; "BB"; "CC"; "DD" ]

    // The trimmed, in-order sequence of the four marker tokens found in `text` (newline + CR + trailing
    // whitespace agnostic — cmd's `echo x 1>&2` leaves a trailing space).
    let tokens (text: string) =
        text.Split('\n')
        |> Array.map (fun s -> s.Trim())
        |> Array.filter tokenSet.Contains
        |> Array.toList

    let collect (items: IAsyncEnumerable<'T>) =
        task {
            let acc = ResizeArray<'T>()
            let e = items.GetAsyncEnumerator()
            let mutable more = true

            while more do
                match! e.MoveNextAsync() with
                | true -> acc.Add e.Current
                | false -> more <- false

            do! e.DisposeAsync()
            return acc
        }

    [<Test>]
    member _.``MergeStderr interleaves stdout and stderr in real order on the single stdout stream``() : Task =
        task {
            match! (alternating |> Command.mergeStderr).OutputStringAsync() with
            | Error error -> Assert.Fail $"{error}"
            | Ok result ->
                // The merged stdout carries every line, in the child's real write order.
                Assert.That(tokens result.Stdout, Is.EqualTo(box [ "AA"; "BB"; "CC"; "DD" ]))
                // There is no separate stderr stream under merge: it is honestly empty.
                Assert.That(result.Stderr, Is.Empty, "stderr must be empty under MergeStderr")
        }
        :> Task

    [<Test>]
    member _.``MergeStderr order is unreachable via the post-hoc Combined of separate streams``() : Task =
        task {
            // Same child, run WITHOUT merge: stdout = AA,CC and stderr = BB,DD are captured separately,
            // so `Combined` concatenates them (all stdout, then all stderr) as AA,CC,BB,DD — never the
            // true interleaving AA,BB,CC,DD that the OS-level merge produces.
            match! alternating.OutputStringAsync() with
            | Error error -> Assert.Fail $"{error}"
            | Ok separate ->
                let combinedOrder = tokens separate.Combined
                Assert.That(combinedOrder, Is.EqualTo(box [ "AA"; "CC"; "BB"; "DD" ]))

                match! (alternating |> Command.mergeStderr).OutputStringAsync() with
                | Error error -> Assert.Fail $"{error}"
                | Ok merged ->
                    let mergedOrder = tokens merged.Stdout
                    Assert.That(mergedOrder, Is.EqualTo(box [ "AA"; "BB"; "CC"; "DD" ]))
                    // The whole point: the honest interleaving differs from the post-hoc concatenation.
                    Assert.That(mergedOrder, Is.Not.EqualTo(box combinedOrder))
        }
        :> Task

    [<Test>]
    member _.``MergeStderr feeds the streaming verbs, emitting only Stdout events``() : Task =
        task {
            match! runner.StartAsync(alternating |> Command.mergeStderr, CancellationToken.None) with
            | Error error -> Assert.Fail $"{error}"
            | Ok running ->
                let! events = collect (running.OutputEventsAsync())

                // Every event is a Stdout event — there is no separate stderr stream to tag.
                let allStdout =
                    events
                    |> Seq.forall (fun e ->
                        match e with
                        | OutputEvent.Stdout _ -> true
                        | OutputEvent.Stderr _ -> false)

                Assert.That(allStdout, Is.True, "every event must be a Stdout event under MergeStderr")

                let order =
                    events
                    |> Seq.map (fun e -> e.Text.Trim())
                    |> Seq.filter tokenSet.Contains
                    |> Seq.toList

                Assert.That(order, Is.EqualTo(box [ "AA"; "BB"; "CC"; "DD" ]))
        }
        :> Task

    [<Test>]
    member _.``MergeStderr streams merged lines through StdoutLinesAsync``() : Task =
        task {
            match! runner.StartAsync(alternating |> Command.mergeStderr, CancellationToken.None) with
            | Error error -> Assert.Fail $"{error}"
            | Ok running ->
                let! lines = collect (running.StdoutLinesAsync())

                let order =
                    lines
                    |> Seq.map (fun l -> l.Trim())
                    |> Seq.filter tokenSet.Contains
                    |> Seq.toList

                Assert.That(order, Is.EqualTo(box [ "AA"; "BB"; "CC"; "DD" ]))
        }
        :> Task

    [<Test>]
    member _.``MergeStderr on the last pipeline stage captures the stage's combined stdout and stderr``() : Task =
        task {
            // A last stage that cats its stdin (the "up" from stage 0) to stdout, then writes ERRLINE to
            // stderr. With merge on, both reach the pipeline's captured output; without it, ERRLINE would
            // be dropped (a pipeline captures only the last stage's stdout).
            let lastStage =
                if isWindows then
                    shell "sort& echo ERRLINE 1>&2" |> Command.mergeStderr
                else
                    shell "cat; echo ERRLINE >&2" |> Command.mergeStderr

            let pipeline = (shell "echo up").Pipe lastStage

            match! pipeline.OutputStringAsync() with
            | Error error -> Assert.Fail $"{error}"
            | Ok result ->
                Assert.That(result.Stdout, Does.Contain "up", "stage-0 stdout must pass through")
                Assert.That(result.Stdout, Does.Contain "ERRLINE", "last stage's merged stderr must be captured")
        }
        :> Task

    [<Test>]
    member _.``MergeStderr rejects StderrTee in either chaining order``() =
        use sink = new MemoryStream()

        Assert.Throws<ArgumentException>(
            Action(fun () -> (Command.create "git" |> Command.mergeStderr).StderrTee sink |> ignore)
        )
        |> ignore

        Assert.Throws<ArgumentException>(
            Action(fun () -> (Command.create "git").StderrTee(sink).MergeStderr() |> ignore)
        )
        |> ignore

    [<Test>]
    member _.``MergeStderr rejects OnStderrLine in either chaining order``() =
        let handler = Action<string>(fun _ -> ())

        Assert.Throws<ArgumentException>(
            Action(fun () -> (Command.create "git" |> Command.mergeStderr).OnStderrLine handler |> ignore)
        )
        |> ignore

        Assert.Throws<ArgumentException>(
            Action(fun () -> (Command.create "git").OnStderrLine(handler).MergeStderr() |> ignore)
        )
        |> ignore

    [<Test>]
    member _.``MergeStderr coexists with the stderr knobs it makes into no-ops``() =
        // StderrEncoding / StderrLineTerminator / Stderr mode are documented no-ops under merge (not
        // rejected — Encoding()/LineTerminator() set the stdout+stderr pair together, so rejecting would
        // make those pair setters conflict with MergeStderr). Building them alongside must not throw.
        Assert.DoesNotThrow(
            Action(fun () ->
                Command.create "git"
                |> Command.mergeStderr
                |> Command.stderrEncoding System.Text.Encoding.Latin1
                |> Command.stderrLineTerminator LineTerminator.Cr
                |> Command.stderr StdioMode.Null
                |> Command.encoding System.Text.Encoding.UTF8
                |> ignore)
        )

    [<Test>]
    member _.``MergeStderr on the last pipeline stage is allowed``() =
        // The last stage's stdout is the pipeline's captured output, so a 2>&1 there is meaningful.
        Assert.DoesNotThrow(
            Action(fun () ->
                (Command.create "sort").Pipe(Command.create "wc" |> Command.mergeStderr)
                |> ignore)
        )

    [<Test>]
    member _.``MergeStderr on a non-last pipeline stage is rejected``() =
        // Stage 0 with merge, demoted immediately by the two-stage Pipe.
        Assert.Throws<ArgumentException>(
            Action(fun () ->
                (Command.create "sort" |> Command.mergeStderr).Pipe(Command.create "wc")
                |> ignore)
        )
        |> ignore

        // A stage that is the last of a two-stage chain (allowed), then demoted by appending a third.
        Assert.Throws<ArgumentException>(
            Action(fun () ->
                (Command.create "sort").Pipe(Command.create "grep" |> Command.mergeStderr).Pipe(Command.create "wc")
                |> ignore)
        )
        |> ignore

    [<Test>]
    member _.``repeated MergeStderr spawns do not leak file descriptors``() : Task =
        task {
            if not isLinux then
                Assert.Ignore "Linux-only: counts open descriptors via /proc/self/fd"

            let openFdCount () =
                Directory.GetFileSystemEntries("/proc/self/fd").Length

            let runOnce () : Task =
                task {
                    // Merge routes fd 2 onto stdout's write end (no separate stderr socketpair): the
                    // child-side dup targets and the parent's retained stdout read end must all be
                    // accounted for and closed, exactly as the plain piped path is (T-002/T-003/T-029).
                    let! _ = (alternating |> Command.mergeStderr).OutputStringAsync()
                    ()
                }

            // Warm up so one-time fds (SIGCHLD/pidfd reaper, thread-pool eventfds, JIT) are already
            // counted in the baseline rather than mistaken for a leak.
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
                $"file descriptors grew from {before} to {after} across 40 merged spawns — likely an fd leak"
            )
        }
        :> Task
