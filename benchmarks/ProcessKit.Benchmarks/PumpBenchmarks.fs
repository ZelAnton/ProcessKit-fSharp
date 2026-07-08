namespace ProcessKit.Benchmarks

open System.IO
open System.Text
open System.Threading
open System.Threading.Tasks
open BenchmarkDotNet.Attributes
open Microsoft.Extensions.Logging.Abstractions
open ProcessKit

/// Micro-benchmarks for the output pump's hot path (`Pump.readLines`), across the four
/// `LineTerminator` framing modes and with/without a `MaxLineLength` force-flush cap, plus the
/// allocation cost of a lifecycle log call when the configured `ILogger`'s level is disabled — the
/// common case, since logging is meant to cost nothing when nobody is listening.
///
/// Every generated line ends with `\r\n`, which every `LineTerminator` mode collapses to a single
/// terminator (see `LineTerminator`'s doc comment), so all four modes split the same payload into
/// the same lines — the benchmark measures each mode's framing overhead on equivalent work, not a
/// difference in how much splitting happens.
[<MemoryDiagnoser>]
type PumpBenchmarks() =

    let lineCount = 500

    let mutable payload: byte[] = Array.empty

    /// The `LineTerminator` values `Terminator` is drawn from (`Params` cannot take discriminated-union
    /// cases directly — they are not compile-time attribute constants — so this goes through
    /// `ParamsSource` instead).
    member _.Terminators: LineTerminator seq =
        seq {
            LineTerminator.Lf
            LineTerminator.Cr
            LineTerminator.CrLf
            LineTerminator.Any
        }

    [<ParamsSource("Terminators")>]
    member val Terminator = LineTerminator.Lf with get, set

    [<Params(16, 4096)>]
    member val LineLength = 16 with get, set

    [<GlobalSetup>]
    member this.Setup() =
        let sb = StringBuilder()

        for _ in 1..lineCount do
            sb.Append(String.replicate this.LineLength "x").Append("\r\n") |> ignore

        payload <- Encoding.UTF8.GetBytes(sb.ToString())

    /// The unbounded hot path: no `MaxLineLength` cap, no tee.
    [<Benchmark(Baseline = true)>]
    member this.ReadLines() : Task =
        task {
            use stream = new MemoryStream(payload)

            do!
                Pump.readLines
                    stream
                    Encoding.UTF8
                    this.Terminator
                    None
                    (fun _ -> ValueTask.CompletedTask)
                    None
                    CancellationToken.None
        }

    /// Same stream, under a `MaxLineLength` cap well below `LineLength` — forces the mid-line,
    /// force-flush segmenting branch of the same hot loop.
    [<Benchmark>]
    member this.ReadLinesCapped() : Task =
        task {
            use stream = new MemoryStream(payload)

            do!
                Pump.readLines
                    stream
                    Encoding.UTF8
                    this.Terminator
                    None
                    (fun _ -> ValueTask.CompletedTask)
                    (Some 8)
                    CancellationToken.None
        }

    /// A lifecycle log call with no `ILogger` configured at all (`Log.spawn None ...`) — the default,
    /// most common shape: short-circuits before touching `LoggerMessage.Define` at all.
    [<Benchmark>]
    member _.LogSpawnNoLogger() =
        Log.spawn None "bench" (Some 4242) "run-id"

    /// A lifecycle log call with an `ILogger` configured but its level disabled (`NullLogger`) —
    /// verifies the cached `LoggerMessage.Define` delegate skips formatting/boxing once the level
    /// check fails, not just that `logger = None` is free.
    [<Benchmark>]
    member _.LogSpawnDisabledLogger() =
        Log.spawn (Some NullLogger.Instance) "bench" (Some 4242) "run-id"
