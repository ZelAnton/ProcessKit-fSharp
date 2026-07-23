![ProcessKit](https://raw.githubusercontent.com/ZelAnton/ProcessKit-fSharp/main/cover.png)

[![CI](https://github.com/ZelAnton/ProcessKit-fSharp/actions/workflows/ci.yml/badge.svg)](https://github.com/ZelAnton/ProcessKit-fSharp/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/ProcessKit.svg)](https://www.nuget.org/packages/ProcessKit)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](https://github.com/ZelAnton/ProcessKit-fSharp/blob/main/LICENSE)
[![.NET](https://img.shields.io/badge/.NET-8.0%20%7C%2010.0-512BD4.svg)](https://dotnet.microsoft.com/)

Async child-process management for .NET with a kernel-backed **no-orphan guarantee**: every
process you start — and everything *it* spawns — lives in a kill-on-dispose container (a
**Windows Job Object**, a **Linux cgroup v2**, or a **POSIX process group**), so no descendant
ever outlives your program.

Beyond spawning a subprocess: run-and-capture, line streaming, interactive stdin, shell-free
pipelines, readiness probes, timeouts & cancellation, supervision with restart/backoff, and a
mockable runner seam for subprocess-free tests.

**F#**

```fsharp
task {
    match! (Command.create "dotnet" |> Command.arg "--version").RunAsync() with
    | Ok version -> printfn $"{version}"
    | Error err -> eprintfn $"{err.Message}"
}
```

**C#**

```csharp
Console.WriteLine(await new Command("dotnet").Arg("--version").RunAsync() switch
{
    { IsOk: true, ResultValue: var version } => version,
    { IsOk: false, ErrorValue: var err }    => $"error: {err.Message}",
});
```

## Why ProcessKit?

`System.Diagnostics.Process` reaches (at most) the direct child. The processes *it* spawned — a
build tool's compiler children, the real payload behind a wrapper (`cmd /c …`, `sh -c …`), a
test's helper servers — survive a timeout, an exception, or a dropped task, and keep running as
orphans.

ProcessKit spawns every child into the operating system's own containment primitive — a **Job
Object** on Windows, a **cgroup v2** on Linux (with a process-group fallback), a **POSIX process
group** on macOS/BSD — so teardown is a kernel operation over the whole tree, not a best-effort
signal to one pid:

- **Nothing escapes silently.** Disposing the handle or group reaps every descendant,
  grandchildren included. Where a mechanism has a genuine weakness (a `setsid` child escapes a
  POSIX process group), the active `Mechanism` is reported instead of pretending — never a silent
  downgrade.
- **Async-first.** Run-and-capture, line streaming, interactive stdin, readiness probes,
  shell-free pipelines, supervision — all return `Task<…>` and stream as `IAsyncEnumerable<…>`.
- **Honest results.** A non-zero exit is data (`ProcessResult`) until you ask for success; a
  timeout is *captured* in the result; a cancellation is always an error; every platform
  divergence is typed or documented.
- **Testable.** One interface seam (`IProcessRunner`) swaps the real spawner for scripted doubles
  or record/replay cassettes — no subprocess in your tests.

## OS-level containment mechanisms

ProcessKit uses the operating system's own kernel containment mechanism rather than app-level
bookkeeping or best-effort signals to individual PIDs:

- **Windows:** a Job Object.
- **Linux:** cgroup v2 when resource limits are requested and available; otherwise a POSIX process
  group.
- **macOS / BSD:** a POSIX process group.

`ProcessGroup.Mechanism` reports which primitive was selected, so code can verify its containment
environment instead of assuming one. See [Process groups](process-groups.md) and
[Platform support](platform-support.md) for the mechanism details and platform caveats.

## How it compares

The comparison is easier to scan as short capability cards than as a table that forces five
columns onto a narrow screen:

- **`System.Diagnostics.Process`** tracks only the direct child. It has no durable whole-tree
  containment, readiness probes, supervision, or injectable runner seam.
- **CliWrap** has an excellent fluent pipeline API and tree-aware cancellation, but no persistent
  `ProcessGroup` for several commands, resource limits, readiness probes, supervision, or runner
  seam.
- **Medallion.Shell** offers straightforward synchronous/async commands and pipelines, but does
  not provide whole-tree containment, typed errors, readiness probes, supervision, or a formal test
  seam.
- **SimpleExec** is intentionally minimal and exception-first: useful for build-script glue, but
  without streaming, pipelines, containment, supervision, or runner substitution.
- **ProcessKit** combines kernel-backed whole-tree containment with honest typed outcomes, async
  streaming, readiness probes, shell-free pipelines, supervision, secret-safe observability, and
  the `IProcessRunner` seam.

See the [Comparison and migration guide](comparison.md) for details and migration recipes.

## Guides

**New here?** Start with the [Cookbook](cookbook.md) — short task-to-snippet recipes for
everything the library does — then read [Running commands](commands.md) end to end (it's the
vocabulary every other guide builds on). Reach for the rest as the need arises, and keep
[Platform support](platform-support.md) handy before you ship: it collects every per-OS caveat in
one place. Deploying to Docker/Kubernetes? [Running in containers](containers.md) collects the
container-specific consequences of that fine print — mechanism selection, `PID 1`, graceful
shutdown, and minimal images.

The repository also includes [runnable sample projects](https://github.com/ZelAnton/ProcessKit-fSharp/tree/main/samples)
for F# and C# users who want compiled examples instead of Markdown snippets.

| Guide | Covers |
|---|---|
| [Comparison and migration guide](comparison.md) | How ProcessKit compares to `System.Diagnostics.Process`, CliWrap, Medallion.Shell, and SimpleExec, plus "was → now" migration snippets |
| [Cookbook](cookbook.md) | "I want to …" → working snippet, for every capability; each recipe links to its deep guide |
| [Running commands](commands.md) | The `Command` builder end to end: args, env, stdin sources, encodings, buffer policies, line handlers, timeouts, retry — and every consuming verb (`RunAsync`, `OutputStringAsync`, `ProbeAsync`, …) with its error semantics |
| [Process groups](process-groups.md) | Kill-on-dispose containment: creating groups, spawning, teardown verbs, whole-tree signals, suspend/resume, member listing, resource limits, stats sampling |
| [Streaming & interactive I/O](streaming.md) | `StartAsync()` and the live `RunningProcess`: line streaming, interactive stdin, readiness probes (`WaitForLineAsync` / `WaitForPortAsync` / `WaitForAsync`), racing children with `WaitAnyAsync`, per-run profiling |
| [Pipelines](pipelines.md) | `a → b → c` without a shell: wiring, pipefail attribution, `UncheckedInPipe` stages for the `… → head` pattern, timeouts, stdin/stdout at the ends |
| [Timeouts, retries & cancellation](timeouts-and-cancellation.md) | How a deadline is *captured* vs when it errors, retry policies and their classifier, and cancellation: per-command tokens and client-level defaults via `CliClient.WithDefaults` |
| [Supervision](supervision.md) | Keeping a child alive: restart policies, backoff & jitter math, the failure-storm guard, stop conditions, outcomes, supervising inside a shared group |
| [Testing your code](testing.md) | The `IProcessRunner` seam — bulk **and** streaming: `ScriptedRunner` (incl. scripted `StartAsync()` with canned lines), record/replay cassettes, and building hermetically-testable CLI wrappers with `CliClient` |
| [Observability](observability.md) | Logging, tracing & metrics: the `ILogger` lifecycle events (EventIds + per-run correlation), the `ProcessKit` `ActivitySource` span, and the `ProcessKit` `Meter` instruments — all secret-safe and OpenTelemetry-ready |
| [Dependency injection](dependency-injection.md) | The `ProcessKit.Extensions.DependencyInjection` and `ProcessKit.Extensions.Hosting` packages: `AddProcessKit` (options / `IConfiguration` defaults), keyed per-tool `CliClient`s, shared `ProcessGroup`s, and supervised hosted processes |
| [Platform support](platform-support.md) | The containment mechanisms, every per-capability support matrix in one place, and the platform caveats worth knowing before you ship |
| [Running in containers](containers.md) | Which `Mechanism` you actually get inside Docker/Kubernetes, running as `PID 1` (signals, reparenting, zombies), graceful shutdown on orchestrator `SIGTERM`, minimal/musl/shell-less images, and container-level limits vs `ProcessGroupOptions` limits |

## The 60-second tour

**F#**

```fsharp
task {
    // One-shot: capture everything. A non-zero exit is data, not an Error.
    match! (Command.create "git" |> Command.args [ "rev-parse"; "HEAD" ]).OutputStringAsync() with
    | Ok head -> printfn $"HEAD = {head.Stdout.Trim()}"
    | Error err -> eprintfn $"{err.Message}"

    // Success-checking: a non-zero exit / timeout / signal-kill becomes a typed error.
    match! (Command.create "dotnet" |> Command.arg "--version").RunAsync() with
    | Ok version -> printfn $"{version}"
    | Error err -> eprintfn $"{err.Message}"

    // Stdin + timeout (streaming, pipelines, supervision … see the guides).
    let sort =
        Command.create "sort"
        |> Command.stdin (Stdin.FromString "b\na\n")
        |> Command.timeout (TimeSpan.FromSeconds 5.0)

    let! _ = sort.RunAsync()

    // Containment: anything spawned through a group dies with it.
    match ProcessGroup.Create() with
    | Ok group ->
        use group = group
        let! _server = group.StartAsync(Command.create "dev-server")
        () // disposing the group reaps the server — and everything it spawned
    | Error err -> eprintfn $"{err.Message}"
}
```

**C#**

```csharp
// One-shot: capture everything. A non-zero exit is data, not an Error.
Console.WriteLine(await new Command("git").Args(["rev-parse", "HEAD"]).OutputStringAsync() switch
{
    { IsOk: true, ResultValue: var head } => $"HEAD = {head.Stdout.Trim()}",
    { IsOk: false, ErrorValue: var err } => err.Message,
});

// Success-checking: a non-zero exit / timeout / signal-kill becomes a typed error.
Console.WriteLine(await new Command("dotnet").Arg("--version").RunAsync() switch
{
    { IsOk: true, ResultValue: var version } => version,
    { IsOk: false, ErrorValue: var err }    => err.Message,
});

// Stdin + timeout (streaming, pipelines, supervision … see the guides).
await new Command("sort")
    .Stdin(Stdin.FromString("b\na\n"))
    .Timeout(TimeSpan.FromSeconds(5))
    .RunAsync();

// Containment: anything spawned through a group dies with it.
using var group = ProcessGroup.Create().GetValueOrThrow();
await group.StartAsync(new Command("dev-server"));
// disposing the group reaps the server — and everything it spawned
```

## API reference

The XML documentation shipped in the package powers IntelliSense / quick-info in your IDE for
every public type and member; these guides are the narrative layer on top — they explain how the
pieces compose, with the platform fine print collected in [Platform support](platform-support.md).
The same XML docs also power a browsable, generated
**[API reference](https://zelanton.github.io/ProcessKit-fSharp/api/)** — published alongside these
guides on the same site — reach for it when you want a member-by-member lookup instead of a
task-oriented guide.

---

Next: [Comparison and migration guide](comparison.md)
