# Observability — logging, tracing & metrics

ProcessKit reports its run lifecycle through the three standard .NET diagnostic channels, all **opt-in
and free when nothing is listening**:

- **`Microsoft.Extensions.Logging`** — structured lifecycle events on an `ILogger` you attach.
- **`System.Diagnostics` tracing** — one `Activity` (span) per completed run, on a named `ActivitySource`.
- **`System.Diagnostics.Metrics`** — counters and a histogram on a named `Meter`.

> **Secrets never leave the process.** argv and environment **values** are never written to a log
> message, a trace tag, or a metric tag. Only the program *name* and non-secret facts (pid, outcome,
> durations, exit code / signal, retry / restart counts, run id) are emitted. This invariant holds across
> all three channels.

## Logging

Attach any `Microsoft.Extensions.Logging.ILogger` with `Command.Logger` (F#: `Command.logger`):

```fsharp
let cmd = Command.create "deploy" |> Command.logger logger
```

```csharp
var cmd = new Command("deploy").Logger(logger);
```

No logger set → no-op, no allocation. Each event is emitted through a cached `LoggerMessage.Define`
delegate, so when the level is disabled there is **no formatting or boxing** on the hot path.

### Event taxonomy

Every event has a stable `EventId` (name + number) so you can filter or route by id — the ids are exposed
as `ProcessKitDiagnostics.Events.*` so you never hard-code a number — and every **run-scoped** event
carries a per-run **`RunId`** (plus the `Pid` on spawn) so a run's lines tie together even across a
concurrent fleet, and even in a sink that does not capture logging scopes.

| Event | `EventId` | `ProcessKitDiagnostics.Events` | Level | Fields |
|---|---|---|---|---|
| Process spawned | `1` `ProcessSpawned` | `.ProcessSpawned` | Debug | program, pid, run id |
| Process exited | `2` `ProcessExited` | `.ProcessExited` | Debug | program, outcome, duration, run id |
| Process timed out | `3` `ProcessTimedOut` | `.ProcessTimedOut` | Warning | program, timeout, run id |
| Run retry | `4` `ProcessRetry` | `.ProcessRetry` | Debug | program, attempt, delay, run id |
| Supervisor restart | `5` `SupervisorRestart` | `.SupervisorRestart` | Debug | program, restart #, delay |
| Supervisor storm pause | `6` `SupervisorStormPause` | `.SupervisorStormPause` | Warning | program, pause |
| Supervisor liveness restart | `7` `SupervisorLivenessRestart` | `.SupervisorLivenessRestart` | Warning | program, consecutive failures |

The `RunId` is stamped once per logical run at the verb layer, so **a run and all its retries share one
id**; a directly-spawned streaming run (`StartAsync`) gets a fresh per-incarnation id. It is a compact,
per-process value — a log sink already scopes by process, and cross-process correlation is the trace's job.

## Tracing

ProcessKit publishes an `ActivitySource` named **`ProcessKitDiagnostics.ActivitySourceName`** (`"ProcessKit"`).
A completed run yields one `processkit.run` span whose duration is the real run length, tagged with:

`processkit.program`, `processkit.run_id`, `processkit.outcome` (`exited` / `signalled` / `timedout`),
`processkit.exit_code` (when it exited), `processkit.signal` (when signalled), `processkit.pid` — **never
argv or environment**. The span nests under whatever `Activity` was current when the run started, so a
run inside an HTTP request appears under that request's trace.

Wire it into OpenTelemetry:

```csharp
services.AddOpenTelemetry().WithTracing(t => t.AddSource(ProcessKitDiagnostics.ActivitySourceName));
```

Free when no listener subscribes (no span is created). A run that is abandoned (spawned, never finished)
emits no span.

## Metrics

ProcessKit publishes a `Meter` named **`ProcessKitDiagnostics.MeterName`** (`"ProcessKit"`), OpenTelemetry-
compatible. Tag cardinality is deliberately bounded — instruments are tagged by **program name** and a
small closed set of **outcome** labels, never by argv.

Units follow the OpenTelemetry/UCUM convention — dimensionless counts use a `{…}` annotation, and the
duration histogram is in **seconds** (the OTel norm for `*.duration`).

| Instrument | Kind | Unit | Tags |
|---|---|---|---|
| `processkit.runs.started` | Counter | `{run}` | program |
| `processkit.runs.completed` | Counter | `{run}` | program, outcome |
| `processkit.runs.active` | UpDownCounter | `{run}` | program |
| `processkit.run.duration` | Histogram | `s` | program, outcome |
| `processkit.retries` | Counter | `{retry}` | program |
| `processkit.supervisor.restarts` | Counter | `{restart}` | program |
| `processkit.supervisor.storm_pauses` | Counter | `{pause}` | program |
| `processkit.supervisor.liveness_restarts` | Counter | `{restart}` | program |

```csharp
services.AddOpenTelemetry().WithMetrics(m => m.AddMeter(ProcessKitDiagnostics.MeterName));
```

`runs.active` is incremented at spawn and decremented when the run's handle stops being "in flight" —
either of two events, whichever happens first: it reaches a terminal verb (`Run`/`Output*`/`Wait*`/
`Profile`/`Finish`, or racing it via `WaitAnyAsync`/`WaitAllAsync`), or its `RunningProcess` handle is
disposed without ever reaching one (a streaming handle dropped without `FinishAsync`). Either way
`runs.active` returns to zero for that run — it never leaks upward just because a caller only streamed
and disposed. Only the first of those two events counts toward `runs.completed`/`run.duration`/the trace
span: a handle disposed without a terminal verb is still not counted as completed ("an abandoned run
simply isn't counted as completed"), so `runs.started`/`runs.completed` can legitimately diverge even
though `runs.active` is exact.

---

Next: [Dependency injection](dependency-injection.md)
