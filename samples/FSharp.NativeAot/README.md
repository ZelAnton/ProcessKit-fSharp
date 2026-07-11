# F# NativeAOT Smoke Sample

A minimal NativeAOT/trimmed consumer of ProcessKit and
`ProcessKit.Extensions.DependencyInjection` that validates the packages' trimming/NativeAOT compatibility
claims (`IsTrimmable`/`IsAotCompatible`) in a real ahead-of-time-compiled image. It:

- spawns a child and captures its stdout (`OutputStringAsync`),
- captures a non-zero exit as an honest result rather than a raised error,
- runs a child inside a kill-on-dispose `ProcessGroup` (exercising the platform containment P/Invoke), and
- resolves an `IProcessRunner` from a DI container (`AddProcessKit`) and runs a child through it.

The `aot-smoke` job in [.github/workflows/ci.yml](../../.github/workflows/ci.yml) publishes this app with
NativeAOT on `linux-x64` and `win-x64` and runs it. Any trim/AOT warning fails the publish
(`TreatWarningsAsErrors` is inherited repo-wide), and a trimmed-away code path breaking at runtime makes
the smoke exit non-zero. See [docs/platform-support.md](../../docs/platform-support.md#trimming-and-nativeaot).

Publish and run it yourself (after building the library) — pick the RID for your OS:

```bash
dotnet build ProcessKit.slnx --configuration Release
dotnet publish samples/FSharp.NativeAot/FSharp.NativeAot.fsproj --configuration Release -r linux-x64
./samples/FSharp.NativeAot/bin/Release/net10.0/linux-x64/publish/FSharp.NativeAot
```

A NativeAOT publish needs the platform C toolchain (`clang`/`zlib` on Linux, the MSVC C++ build tools on
Windows). A plain `dotnet run` works too if you only want to exercise the scenario without the native image.
