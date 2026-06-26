# ProcessKit

[![CI](https://github.com/ZelAnton/ProcessKit-fSharp/actions/workflows/ci.yml/badge.svg)](https://github.com/ZelAnton/ProcessKit-fSharp/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/ProcessKit.svg)](https://www.nuget.org/packages/ProcessKit)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4.svg)](https://dotnet.microsoft.com/)

F# async child-process management for .NET: whole-tree kill-on-drop (no orphans),
streaming, pipelines, timeouts, and supervision.

> **Status: F# rewrite, first release 2.0.0.** ProcessKit 2.x is a ground-up F# reimplementation
> that supersedes the author's earlier C# `ProcessKit` package (published through 1.3.2). It is an
> F# port of the Rust crate `ProcessKit-rs`, built feature by feature against that crate's stable
> v1.0.1 surface; expect API refinements until the 2.0.0 release lands. See `CHANGELOG.md` for what
> has landed.

## Install

```bash
dotnet add package ProcessKit
# optional — Microsoft.Extensions.DependencyInjection integration (AddProcessKit)
dotnet add package ProcessKit.Extensions.DependencyInjection
```

## Quick start

Every run returns `Task<Result<_, ProcessError>>` — a non-zero exit is *data* for the
capture verbs (`OutputString`/`OutputBytes`/`ExitCode`/`Probe`) and an *error* only for the
success-requiring verbs (`Run`/`RunUnit`).

```fsharp
open ProcessKit

task {
    // Require a zero exit; return stdout, trailing whitespace trimmed.
    let head = Command.create "git" |> Command.args [ "rev-parse"; "HEAD" ]

    match! head.Run() with
    | Ok sha -> printfn $"HEAD is {sha}"
    | Error err -> eprintfn $"{err.Message}"

    // Shell-free pipeline: each stage's stdout feeds the next stage's stdin, all in one
    // kill-on-dispose group. The exit status follows pipefail.
    let pipeline =
        (Command.create "cat" |> Command.arg "access.log")
            .Pipe(Command.create "grep" |> Command.arg "ERROR")
            .Pipe(Command.create "wc" |> Command.arg "-l")

    match! pipeline.Run() with
    | Ok count -> printfn $"{count} error lines"
    | Error err -> eprintfn $"{err.Message}"
}
```

From C# the same surface is available as fluent methods (`command.Run()`,
`command.Pipe(next).OutputString()`, …).

## Highlights

- **Whole-tree kill-on-drop** — a process and everything it spawns is reaped on dispose
  (Windows Job Object `KILL_ON_JOB_CLOSE`; Linux/macOS POSIX process group), or on GC
  finalization as a safety net.
- **Honest results** — `ProcessError` distinguishes spawn / not-found / non-zero exit /
  signal / timeout / cancellation; the verb you choose decides whether a non-zero exit is
  data or an error.
- **Streaming & interactive I/O** — `Command.Start()` returns a live `RunningProcess` with
  `StdoutLines()` / `OutputEvents()` as `IAsyncEnumerable`, interactive stdin, and readiness
  probes (`WaitForLine` / `WaitForPort` / `WaitFor`).
- **Timeouts, cancellation, retry** — `Command.Timeout` / `TimeoutGrace` / `CancelOn` /
  `Retry`, plus `Command.OkCodes` to accept non-zero exits as success.
- **Shell-free pipelines** — `Command.Pipe` with pipefail semantics and `UncheckedInPipe`.
- **Supervision** — `Supervisor` keeps a command alive with restart policies, exponential
  backoff + jitter, and a failure-storm guard.
- **Tree control & resource limits** — `ProcessGroup.Signal` / `Suspend` / `Resume` /
  `Members`, and `ProcessGroup.Create(options)` with `ResourceLimits` (memory / process
  count / CPU) enforced by a Windows Job Object or a Linux cgroup v2.
- **Stats & profiling** — `ProcessGroup.Stats` / `SampleStats` and `RunningProcess.Profile`
  (CPU / peak memory).
- **Ergonomics** — `CliClient` (a program with shared defaults), top-level `Exec.run` /
  `Exec.outputAll`, and shared-group running (`ProcessGroup` is itself an `IProcessRunner`).
- **Observability** — optional `Command.WithLogger` lifecycle events (argv/env never logged),
  and `ProcessKit.Extensions.DependencyInjection`'s `AddProcessKit`.
- **Testable** — `ProcessKit.Testing.ScriptedRunner` and `RecordReplayRunner` (record/replay
  cassettes) are subprocess-free `IProcessRunner`s for hermetic tests.

## Contributing

Issues and pull requests are welcome — see [CONTRIBUTING.md](CONTRIBUTING.md) and the
[Code of Conduct](CODE_OF_CONDUCT.md). To report a security issue, follow [SECURITY.md](SECURITY.md).

## Links

- [Changelog](CHANGELOG.md) — what has shipped.
- [Roadmap](ROADMAP.md) — what is planned.

## License

[MIT](LICENSE) © Anton Zhelezniakou
