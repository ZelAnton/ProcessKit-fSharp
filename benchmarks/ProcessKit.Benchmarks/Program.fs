module ProcessKit.Benchmarks.Program

open BenchmarkDotNet.Configs
open BenchmarkDotNet.Exporters.Json
open BenchmarkDotNet.Jobs
open BenchmarkDotNet.Running
open BenchmarkDotNet.Toolchains.InProcess.NoEmit

/// Run every benchmark in-process, rather than BenchmarkDotNet's default out-of-process toolchain
/// (which regenerates an isolated project per run). That regeneration only copies well-known
/// `PackageReference`/`ProjectReference` items from the original `.fsproj`; it has no way to replicate
/// this repo's convention of a bare `<Reference Include="ProcessKit" />` resolved via
/// `AssemblySearchPaths` (see CLAUDE.md), so the regenerated project fails to reference `ProcessKit`
/// at all. In-process reflects directly over this already-built assembly instead of recompiling
/// anything, sidestepping the mismatch entirely (the standard trade-off: results share this process's
/// GC/JIT state with the harness, instead of running in full isolation).
let private inProcessToolchain = InProcessNoEmitToolchain.Instance

/// Default, full-fidelity local configuration: BenchmarkDotNet's normal statistically-rigorous job
/// (multiple pilot/warmup/workload iterations until the results stabilize).
let private defaultConfig =
    ManualConfig.Create(DefaultConfig.Instance).AddJob(Job.Default.WithToolchain(inProcessToolchain))

/// Reduced-iteration configuration for the scheduled/manual CI benchmark workflow
/// (`.github/workflows/benchmarks.yml`), selected via the `--ci` flag below. `Job.ShortRun` cuts
/// pilot/warmup/workload iteration counts (roughly 3 of each) instead of running until the results
/// statistically stabilize: on a shared hosted runner, absolute numbers are noisy regardless, so
/// this trades per-run precision for a run that finishes in minutes rather than tens of minutes —
/// good enough to eyeball a trend or catch a gross regression, not to certify an exact number (see
/// docs/internals/architecture.md's benchmarking section for how to read the results). `JsonExporter.Full`
/// attaches the machine-readable export the CI workflow uploads as an artifact.
let private ciConfig =
    ManualConfig
        .Create(DefaultConfig.Instance)
        .AddJob(Job.ShortRun.WithToolchain(inProcessToolchain))
        .AddExporter(JsonExporter.Full)

/// Entry point: run all benchmark classes in this assembly, or the subset selected on the command
/// line (`dotnet run -c Release -- --filter *Pump*`). Not part of `dotnet test`/CI's required job —
/// invoke manually, or add `--ci` (consumed here, not forwarded to BenchmarkDotNet's own argument
/// parser) to run the short/CI configuration used by the scheduled `benchmarks.yml` workflow.
[<EntryPoint>]
let main argv =
    let isCi = argv |> Array.contains "--ci"
    let remainingArgv = argv |> Array.filter (fun arg -> arg <> "--ci")
    let config = if isCi then ciConfig else defaultConfig

    BenchmarkSwitcher.FromAssembly(typeof<PumpBenchmarks>.Assembly).Run(remainingArgv, config)
    |> ignore

    0
