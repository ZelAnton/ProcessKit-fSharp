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
go into it** — an easy-to-miss mis-wire. `AddProcessKitGroup(configure)` / `AddProcessKitGroup(configuration)`
apply the same `ProcessKitOptions` defaults as the `AddProcessKit` overloads.

## Hosting a supervised child

A hosted-service wrapper that keeps a [`Supervisor`](supervision.md) command alive for the app's lifetime
is a natural companion, but it needs `Microsoft.Extensions.Hosting.Abstractions`. To keep this package
hosting-free it is **not** included here; today, run a `Supervisor` from your own `IHostedService`
(inject the DI-registered `IProcessRunner` via `Supervisor.WithRunner`).
