namespace ProcessKit.Benchmarks

open System.Collections.Generic
open System.Diagnostics
open System.Runtime.InteropServices
open System.Threading.Tasks
open BenchmarkDotNet.Attributes
open CliWrap
open CliWrap.Buffered
open ProcessKit

/// Shared shell plumbing for the three comparison scenarios below (`SingleSpawnCaptureBenchmarks`,
/// `StreamingBenchmarks`, `ConcurrentBatchBenchmarks`): every scenario runs the *same* shell payload
/// on ProcessKit, the zero-dependency `System.Diagnostics.Process` baseline, and CliWrap, so the
/// three implementations are always compared against identical work. See `docs/comparison.md` for
/// the qualitative write-up these numbers back up.
///
/// MedallionShell/SimpleExec are intentionally NOT included here. SimpleExec has no line-streaming
/// API at all (see its section of `docs/comparison.md`), so scenario 2 (streaming) has nothing
/// equivalent to measure faithfully; MedallionShell's own async surface is shaped differently enough
/// (synchronous-first `Command.Run`, its own `Command.GetOutputAndErrorLines()` streaming idiom) that
/// wiring it up to the *same* three scenarios without either favoring or short-changing it would need
/// more benchmark-project surface than this comparison-only, non-shipping project's scope justifies
/// right now. If that trade-off changes, add `MedallionBenchmarks` alongside these three following the
/// same `ComparisonShell` plumbing.
module private ComparisonShell =

    let isWindows = RuntimeInformation.IsOSPlatform OSPlatform.Windows

    let shellExe = if isWindows then "cmd.exe" else "/bin/sh"

    /// `cmd.exe /c "<script>"` on Windows, `/bin/sh -c "<script>"` elsewhere — the same convention
    /// `ConcurrencyBenchmarks` and the test suite use.
    let shellArgs (script: string) : string[] =
        if isWindows then [| "/c"; script |] else [| "-c"; script |]

    /// Scenario 1 ("single spawn + capture") and scenario 3 ("concurrent batch") payload: a single
    /// short line, identical across all three implementations.
    let echoScript = "echo benchmark"

    /// How many lines scenario 2's child prints — large enough that the line-reading loop, not
    /// process start/teardown, dominates the measurement.
    let streamedLineCount = 2000

    /// Scenario 2 ("streaming a large volume of output") payload: a shell one-liner that prints
    /// `streamedLineCount` lines with no external dependency (`for /L` on `cmd.exe`; a POSIX `while`
    /// loop on `/bin/sh`, since `seq` is not guaranteed present on every `/bin/sh`).
    let streamingScript =
        if isWindows then
            sprintf "for /L %%i in (1,1,%d) do @echo line %%i of the comparison streaming payload" streamedLineCount
        else
            sprintf
                "i=1; while [ $i -le %d ]; do echo \"line $i of the comparison streaming payload\"; i=$((i + 1)); done"
                streamedLineCount

    /// ProcessKit's `Command`, via the platform shell.
    let pkCommand (script: string) =
        Command.create shellExe |> Command.args (shellArgs script)

    /// A redirected `ProcessStartInfo`, via the platform shell — the raw `System.Diagnostics.Process`
    /// baseline every scenario below compares against.
    let processStartInfo (script: string) =
        let info =
            ProcessStartInfo(shellExe, RedirectStandardOutput = true, UseShellExecute = false)

        for arg in shellArgs script do
            info.ArgumentList.Add arg

        info

    /// CliWrap's `Command`, via the platform shell. Annotated with the fully-qualified
    /// `CliWrap.Command` — `ProcessKit` (opened below, after `CliWrap`) also defines a `Command`
    /// type, and the later `open` wins the unqualified name.
    let cliWrapCommand (script: string) : CliWrap.Command =
        Cli.Wrap(shellExe).WithArguments(shellArgs script: string seq)

    /// Drain a ProcessKit `IAsyncEnumerable<string>` to completion, counting lines — the same
    /// enumerator-loop-then-dispose shape `StreamingTests.fs`'s `collect` helper uses, minus the
    /// accumulation (the benchmark only needs the loop's cost, not the retained lines).
    let countLinesAsync (lines: IAsyncEnumerable<string>) : Task<int> =
        task {
            let enumerator = lines.GetAsyncEnumerator()
            let mutable more = true
            let mutable count = 0

            while more do
                let! has = enumerator.MoveNextAsync()
                if has then count <- count + 1 else more <- false

            do! enumerator.DisposeAsync()
            return count
        }

/// Scenario 1: spawn one short-lived child and capture its stdout — the most common
/// "run this, get the output" shape.
[<MemoryDiagnoser>]
type SingleSpawnCaptureBenchmarks() =

    [<Benchmark(Baseline = true)>]
    member _.RawProcess() : Task =
        task {
            use proc =
                new Process(StartInfo = ComparisonShell.processStartInfo ComparisonShell.echoScript)

            proc.Start() |> ignore
            let! _stdout = proc.StandardOutput.ReadToEndAsync()
            do! proc.WaitForExitAsync()
        }

    [<Benchmark>]
    member _.ProcessKit() : Task =
        task {
            match! (ComparisonShell.pkCommand ComparisonShell.echoScript).OutputStringAsync() with
            | Ok _ -> ()
            | Error err -> failwith $"ProcessKit run failed: {err.Message}"
        }

    [<Benchmark>]
    member _.CliWrap() : Task =
        task {
            let! _ = (ComparisonShell.cliWrapCommand ComparisonShell.echoScript).ExecuteBufferedAsync()
            ()
        }

/// Scenario 2: stream a large volume of stdout, line by line, without buffering the whole payload —
/// the shape `RunningProcess.StdoutLinesAsync()`, CliWrap's `PipeTarget.ToDelegate`, and
/// `StreamReader.ReadLineAsync()` are each built for.
[<MemoryDiagnoser>]
type StreamingBenchmarks() =

    [<Benchmark(Baseline = true)>]
    member _.RawProcess() : Task =
        task {
            use proc =
                new Process(StartInfo = ComparisonShell.processStartInfo ComparisonShell.streamingScript)

            proc.Start() |> ignore
            let mutable more = true
            let mutable count = 0

            while more do
                let! line = proc.StandardOutput.ReadLineAsync()

                match line with
                | null -> more <- false
                | _ -> count <- count + 1

            do! proc.WaitForExitAsync()
        }

    [<Benchmark>]
    member _.ProcessKit() : Task =
        task {
            match! (ComparisonShell.pkCommand ComparisonShell.streamingScript).StartAsync() with
            | Ok running ->
                use _ = running
                let! _count = ComparisonShell.countLinesAsync (running.StdoutLinesAsync())

                match! running.FinishAsync() with
                | Ok _ -> ()
                | Error err -> failwith $"ProcessKit run failed: {err.Message}"
            | Error err -> failwith $"ProcessKit start failed: {err.Message}"
        }

    [<Benchmark>]
    member _.CliWrap() : Task =
        task {
            let mutable count = 0
            let target = PipeTarget.ToDelegate(fun (_line: string) -> count <- count + 1)

            let! _ =
                (ComparisonShell.cliWrapCommand ComparisonShell.streamingScript)
                    .WithStandardOutputPipe(target)
                    .ExecuteAsync()

            ()
        }

/// Scenario 3: fan out `FanOut` short-lived children concurrently and capture each one's stdout —
/// the same shape `ConcurrencyBenchmarks.ConcurrentOutputStringAsync` measures for ProcessKit alone,
/// here compared against the other two implementations.
[<MemoryDiagnoser; ThreadingDiagnoser>]
type ConcurrentBatchBenchmarks() =

    [<Params(2, 8)>]
    member val FanOut = 2 with get, set

    [<Benchmark(Baseline = true)>]
    member this.RawProcess() : Task =
        let runOne () : Task =
            task {
                use proc =
                    new Process(StartInfo = ComparisonShell.processStartInfo ComparisonShell.echoScript)

                proc.Start() |> ignore
                let! _stdout = proc.StandardOutput.ReadToEndAsync()
                do! proc.WaitForExitAsync()
            }

        Array.init this.FanOut (fun _ -> runOne ()) |> Task.WhenAll

    [<Benchmark>]
    member this.ProcessKit() : Task =
        task {
            let commands =
                Array.init this.FanOut (fun _ -> ComparisonShell.pkCommand ComparisonShell.echoScript)

            let! results = commands |> Array.map (fun c -> c.OutputStringAsync()) |> Task.WhenAll

            for result in results do
                match result with
                | Ok _ -> ()
                | Error err -> failwith $"ProcessKit run failed: {err.Message}"
        }

    [<Benchmark>]
    member this.CliWrap() : Task =
        let runOne () : Task =
            task {
                let! _ = (ComparisonShell.cliWrapCommand ComparisonShell.echoScript).ExecuteBufferedAsync()
                ()
            }

        Array.init this.FanOut (fun _ -> runOne ()) |> Task.WhenAll
