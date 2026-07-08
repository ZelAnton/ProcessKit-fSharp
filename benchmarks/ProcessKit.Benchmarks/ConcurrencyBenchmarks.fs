namespace ProcessKit.Benchmarks

open System
open System.Runtime.InteropServices
open System.Threading.Tasks
open BenchmarkDotNet.Attributes
open ProcessKit

/// Concurrent fan-out over the default runner: `FanOut` simultaneous runs, with `ThreadingDiagnoser`
/// (thread-pool occupancy) and `MemoryDiagnoser` (allocations) attached. This is the objective
/// baseline the roadmap's async POSIX pipe / pidfd-wait stages are meant to move (see
/// `docs/planning/post-2.0-plan.md`): both are best judged by how much thread-pool occupancy and
/// memory a concurrent fleet of runs costs, not by a single run's latency.
///
/// Each iteration spawns `FanOut` real, short-lived shell child processes — deliberately modest
/// `FanOut` values, since this is measuring ProcessKit's own concurrency handling, not stress-testing
/// the OS process table.
[<MemoryDiagnoser; ThreadingDiagnoser>]
type ConcurrencyBenchmarks() =

    let isWindows = RuntimeInformation.IsOSPlatform OSPlatform.Windows

    let shell (script: string) =
        if isWindows then
            Command.create "cmd.exe" |> Command.args [ "/c"; script ]
        else
            Command.create "/bin/sh" |> Command.args [ "-c"; script ]

    let echoCommand () = shell "echo benchmark"

    /// Dispose every handle in `processes` concurrently (`RunningProcess` is `IAsyncDisposable` only).
    let disposeAll (processes: RunningProcess[]) : Task =
        processes
        |> Array.map (fun p -> (p :> IAsyncDisposable).DisposeAsync().AsTask())
        |> Task.WhenAll

    [<Params(2, 8)>]
    member val FanOut = 2 with get, set

    /// `FanOut` concurrent buffered runs via `OutputStringAsync` — the common "spawn, capture stdout,
    /// done" fan-out shape.
    [<Benchmark(Baseline = true)>]
    member this.ConcurrentOutputStringAsync() : Task =
        task {
            let commands = Array.init this.FanOut (fun _ -> echoCommand ())
            let! _ = commands |> Array.map (fun c -> c.OutputStringAsync()) |> Task.WhenAll
            ()
        }

    /// `FanOut` concurrent live handles started via `StartAsync`, then raced together with
    /// `RunningProcess.WaitAllAsync` — the streaming/interactive fan-out shape.
    [<Benchmark>]
    member this.ConcurrentStartThenWaitAll() : Task =
        task {
            let commands = Array.init this.FanOut (fun _ -> echoCommand ())
            let! started = commands |> Array.map (fun c -> c.StartAsync()) |> Task.WhenAll

            let running =
                started
                |> Array.map (function
                    | Ok r -> r
                    | Error e -> failwith $"StartAsync failed: {e.Message}")

            let! _ = RunningProcess.WaitAllAsync running
            do! disposeAll running
        }
