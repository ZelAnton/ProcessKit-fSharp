# F# Catalog Sample

Runnable catalog of ProcessKit scenarios in one F# console app:

- `RunAsync` for success-required output and `OutputStringAsync` for honest non-zero exit data.
- `StartAsync` plus `WaitForLineAsync` for streaming readiness.
- `Command.Pipe` for a shell-free pipeline.
- `Supervisor` with restart/backoff driven by `ScriptedRunner`.
- `IProcessRunner` as a subprocess-free test seam.

Run from the repository root after building the library:

```bash
dotnet run --project samples/FSharp.Catalog/FSharp.Catalog.fsproj --framework net10.0
```
