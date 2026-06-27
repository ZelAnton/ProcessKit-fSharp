namespace ProcessKit.Testing

open System
open System.Diagnostics
open System.IO
open System.Threading.Tasks
open ProcessKit

/// Builds an in-memory `RunningProcess` for unit-testing code that consumes a live handle —
/// `StdoutLines` / `OutputEvents` / `Finish` / the readiness probes / the buffered verbs — without
/// spawning a real process. Immutable and fluent; `Build()` returns a real `RunningProcess` whose
/// stdout/stderr are `MemoryStream`s of the scripted text, whose wait resolves to the scripted
/// outcome, and whose kill/teardown are no-ops.
[<Sealed>]
type FakeProcess private (program: string, stdout: string, stderr: string, outcome: Outcome, pid: int option) =

    /// A fake of `program` that exits 0 with no output.
    static member Create(program: string) =
        FakeProcess(program, "", "", Outcome.Exited 0, None)

    /// A fake (named `"fake"`) that exits 0 with no output.
    static member Create() = FakeProcess.Create "fake"

    /// The captured stdout the fake replays (split on `\n` into lines for the streaming verbs).
    member _.WithStdout(text: string) =
        FakeProcess(program, text, stderr, outcome, pid)

    /// The captured stdout as a sequence of lines (joined with `\n`).
    member _.WithStdoutLines(lines: seq<string>) =
        FakeProcess(program, String.Join('\n', lines), stderr, outcome, pid)

    /// The captured stderr.
    member _.WithStderr(text: string) =
        FakeProcess(program, stdout, text, outcome, pid)

    /// Make the fake exit with `code`.
    member _.WithExit(code: int) =
        FakeProcess(program, stdout, stderr, Outcome.Exited code, pid)

    /// Make the fake conclude with an explicit `Outcome` (e.g. `Outcome.TimedOut` or `Signalled`).
    member _.WithOutcome(value: Outcome) =
        FakeProcess(program, stdout, stderr, value, pid)

    /// Set the pid the handle reports.
    member _.WithPid(value: int) =
        FakeProcess(program, stdout, stderr, outcome, Some value)

    /// Build a real `RunningProcess` over in-memory streams.
    member _.Build() : RunningProcess =
        let config = (Command.create program).Config

        let stdoutStream = new MemoryStream(config.StdoutEncoding.GetBytes stdout) :> Stream
        let stderrStream = new MemoryStream(config.StderrEncoding.GetBytes stderr) :> Stream

        let host: RunningHost =
            { Config = config
              Pid = pid
              Stdout = Some stdoutStream
              Stderr = Some stderrStream
              Stdin = None
              StartTime = DateTime.UtcNow
              StartedTimestamp = Stopwatch.GetTimestamp()
              Wait = fun () -> Task.FromResult outcome
              StartKill = fun () -> ()
              GracefulKill = fun _ -> Task.CompletedTask
              Teardown =
                fun () ->
                    stdoutStream.Dispose()
                    stderrStream.Dispose()
                    ValueTask.CompletedTask }

        new RunningProcess(host)
