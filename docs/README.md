# ProcessKit — documentation

ProcessKit is a child-process toolkit for .NET in two layers:

```text
┌─────────────────────────────────────────────────────────────────┐
│  Runner layer (async, Task)                                      │
│  Command · RunningProcess · Pipeline · Supervisor · CliClient    │
│  capture / streaming / interactive stdin / readiness probes      │
│  testing seam: IProcessRunner → ScriptedRunner / RecordReplay…   │
├─────────────────────────────────────────────────────────────────┤
│  Group layer (kill-on-dispose containment)                       │
│  ProcessGroup: spawn / signal / suspend / members / stats /      │
│  limits / shutdown                                               │
├─────────────────────────────────────────────────────────────────┤
│  OS mechanisms                                                   │
│  Windows Job Object · Linux cgroup v2 · POSIX process group      │
└─────────────────────────────────────────────────────────────────┘
```

Every `Command` run gets containment for free: the one-shot verbs spawn into a fresh private
group that dies with the run, so an early return or an unhandled exception never leaks a process
tree. The layers are also usable independently — a raw `ProcessGroup` can contain children you
spawn yourself, and the runner's test doubles never touch the OS at all.

> **Written in F#, built for both F# and C#.** ProcessKit is implemented in F#, but it is designed
> for first-class, idiomatic use from **both F# and C#** — every public API is meant to be called
> naturally from either language, and every example in these guides is shown in both.

## Guides

**New here?** Start with the [Cookbook](cookbook.md) — short task-to-snippet recipes for
everything the library does — then read [Running commands](commands.md) end to end (it's the
vocabulary every other guide builds on). Reach for the rest as the need arises, and keep
[Platform support](platform-support.md) handy before you ship: it collects every per-OS caveat in
one place.

| Guide | Covers |
|---|---|
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
**[API reference](https://zelanton.github.io/ProcessKit-fSharp/)** — reach for it when you want a
member-by-member lookup instead of a task-oriented guide.
