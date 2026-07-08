module ProcessKit.Benchmarks.Program

open BenchmarkDotNet.Configs
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
let private config =
    ManualConfig.Create(DefaultConfig.Instance).AddJob(Job.Default.WithToolchain(InProcessNoEmitToolchain.Instance))

/// Entry point: run all benchmark classes in this assembly, or the subset selected on the command
/// line (`dotnet run -c Release -- --filter *Pump*`). Not part of `dotnet test`/CI — invoke manually.
[<EntryPoint>]
let main argv =
    BenchmarkSwitcher.FromAssembly(typeof<PumpBenchmarks>.Assembly).Run(argv, config)
    |> ignore

    0
