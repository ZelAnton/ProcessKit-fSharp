# C# Worker Service Sample

Runnable Generic Host application showing `ProcessKit.Extensions.Hosting` end to end:

- `AddProcessKitHostedProcess` supervises a long-lived child and restarts it after a crash.
- The sample executable launches itself with `--child`, so it has no external runtime dependency.
- `AddProcessKitHostedProcessHealthCheck` exposes the live supervision state; a background reporter
  polls the keyed check and writes health, restart, and storm-pause telemetry through `ILogger`.
- Host shutdown gives the child five seconds to stop gracefully through an explicit `Signal.Term`
  before ProcessKit escalates to a hard tree kill. `WindowsCtrlSignals` enables the corresponding
  CTRL+BREAK path on Windows and is a no-op on POSIX.

Run from the repository root:

```bash
dotnet run --project samples/CSharp.WorkerService/CSharp.WorkerService.csproj --framework net10.0
```

The parent and child emit periodic log messages. Press Ctrl+C once: the parent Generic Host begins
shutdown, ProcessKit stops the active child within the configured grace period, and both processes exit.
