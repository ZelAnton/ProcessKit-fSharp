# Dependency injection

The `ProcessKit.Extensions.DependencyInjection` package wires ProcessKit into
`Microsoft.Extensions.DependencyInjection`. It stays **dependency-light** — only the DI, Logging,
Options, and Configuration extension packages that a DI-integration package inevitably needs, and **no
hosting dependency** — and every registration uses `TryAdd`, so a pre-existing registration of yours
always wins.

## The runner

`AddProcessKit()` registers `IProcessRunner` as a singleton `JobRunner`. When the container also has an
`ILoggerFactory`, the runner is wrapped so every run it drives emits ProcessKit's lifecycle events under
the `ProcessKit` category (argv/env never logged — see [Observability](observability.md)).

```csharp
services.AddProcessKit();

// Injected anywhere:
public class Deployer(IProcessRunner runner)
{
    public Task<FSharpResult<string, ProcessError>> Deploy() =>
        runner.RunAsync(new Command("deploy"), CancellationToken.None);
}
```

## Default settings (`ProcessKitOptions`)

Configure defaults applied to **every** DI-resolved run — from code or from configuration. Each default
is applied **only when the command does not set it itself**, so a per-command value always wins.

```csharp
// From code:
services.AddProcessKit(o =>
{
    o.DefaultTimeout = TimeSpan.FromSeconds(30);
    o.DefaultWorkingDirectory = "/app";
});

// …or bound from an IConfiguration section (appsettings.json "ProcessKit"):
services.AddProcessKit(configuration.GetSection("ProcessKit"));
```

`ProcessKitOptions` covers what a *primitive* runner can apply on the spawn path — timeout and working
directory. **Retry is a verb-layer policy** (the retry loop reads the command before this runner sees it),
so a retry default can't ride on the bare runner; set it — and richer per-tool defaults like encoding,
ok-codes, and environment — on a **named client** instead, whose template precedes the verb:

```csharp
services.AddProcessKitClient("git", "git",
    c => c.WithDefaults(cmd => cmd.Retry(3, TimeSpan.FromSeconds(1), e => e.IsTransient)));
```

## Named / keyed tool clients

Register a keyed `CliClient` per external tool, so an app injects "the git client" or "the ffmpeg client"
by role. Each client runs through the container's registered `IProcessRunner` (so it is logger-aware and
honours a shared group or a test runner), and `configure` applies shared defaults via the `CliClient`
builder.

```csharp
services.AddProcessKit();
services.AddProcessKitClient("git", "git", c => c.WithDefaults(cmd => cmd.CurrentDir("/repo")));
services.AddProcessKitClient("ffmpeg", "ffmpeg");

public class Repo([FromKeyedServices("git")] CliClient git)
{
    public Task<FSharpResult<string, ProcessError>> Status() => git.RunAsync(["status"]);
}
```

## A shared, container-managed process group

`AddProcessKitGroup()` backs `IProcessRunner` with a single shared `ProcessGroup` whose lifetime is the
container's — every run goes into one kill-on-dispose container, and **disposing the provider reaps the
whole tree**. Ideal for a hosted service that should leave no orphaned children when it stops. The
`ProcessGroup` is also registered directly, so you can inject it for tree control (`Signal` / `Suspend` /
`Members` / …). Call it **instead of** `AddProcessKit()` when you want a shared group.

```csharp
services.AddProcessKitGroup();
// IProcessRunner now runs every command into the shared group;
// await using the provider (or host shutdown) reaps all children.
```

Both `AddProcessKitGroup()` and `AddProcessKit()` register `IProcessRunner` with `TryAdd`, so call **one
or the other** — whichever runs first wins. If `AddProcessKit()` runs first, `IProcessRunner` stays the
per-run `JobRunner`, and a later `AddProcessKitGroup()` still registers the `ProcessGroup` but **no runs
go into it** — an easy-to-miss wiring error. `AddProcessKitGroup(configure)` / `AddProcessKitGroup(configuration)`
apply the same `ProcessKitOptions` defaults as the `AddProcessKit` overloads.

## Hosting a supervised child

Use the `ProcessKit.Extensions.Hosting` package when a supervised child should live for the host's
lifetime. It depends only on `Microsoft.Extensions.Hosting.Abstractions`, discovers an existing
DI-registered `IProcessRunner` when one is present, starts `Supervisor.RunAsync` in the background, and
calls `RunningProcess.StopAsync` during host shutdown.

```csharp
services.AddProcessKitGroup();

services.AddProcessKitHostedProcess(
    "worker",
    new Command("worker").Arg("--serve"),
    supervisor => supervisor
        .Restart(RestartPolicy.OnCrash)
        .OnRestart(e => metrics.Restarts.Add(1)));

services.ConfigureProcessKitHostedProcess("worker", o =>
{
    o.ShutdownGracePeriod = TimeSpan.FromSeconds(10);
});
```

Resolve `HostedProcessService` by the same key when you need the last `SupervisionOutcome` or stop
outcome for health reporting. It also exposes live supervision telemetry — `IsSupervisionActive`,
`RestartCount`, `IsStormPaused` — for anything that wants to observe the child without waiting for
supervision to end (e.g. metrics, or the health check below).

## Health-checking a hosted process

`AddProcessKitHostedProcessHealthCheck(name)` registers a keyed `IHealthCheck`
(`HostedProcessHealthCheck`, same key as `AddProcessKitHostedProcess`) that maps the named hosted
process's supervision state: **Healthy** while it is running (including restarting within policy),
**Degraded** while the failure-storm guard (`Supervisor.StormPause`) is throttling restarts, and
**Unhealthy** once supervision is not active (not started yet, or ended — an error, an exhausted
restart budget, a permanent-failure give-up, or a stop-predicate match).

This is opt-in and stays in `ProcessKit.Extensions.Hosting` (not a separate package): its only extra
dependency, `Microsoft.Extensions.Diagnostics.HealthChecks.Abstractions`, is Abstractions-only —
`IHealthCheck` / `HealthCheckResult` / `HealthCheckRegistration`, never the full
`Microsoft.Extensions.Diagnostics.HealthChecks` package that supplies `AddHealthChecks()` /
`IHealthChecksBuilder` / the concrete polling `HealthCheckService`. That package stays out of this
one's dependency graph, so a consumer who never calls `AddProcessKitHostedProcessHealthCheck` never
pulls it in either — but it also means this method cannot call `AddHealthChecks()` on your behalf.
Wire the registered keyed check into your own health-checks pipeline (already referenced
transitively via the ASP.NET Core shared framework in a web host; add
`Microsoft.Extensions.Diagnostics.HealthChecks` explicitly in a Worker Service) with
`HealthCheckRegistration`'s factory overload:

```csharp
services.AddProcessKitHostedProcess("worker", new Command("worker").Arg("--serve"));
services.AddProcessKitHostedProcessHealthCheck("worker");

services.AddHealthChecks().Add(
    new HealthCheckRegistration(
        "worker",
        sp => sp.GetRequiredKeyedService<HostedProcessHealthCheck>("worker"),
        failureStatus: null,
        tags: null));
```

---

Next: [Platform support](platform-support.md)
