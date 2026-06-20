# ProcessKit

F# async child-process management for .NET: whole-tree kill-on-drop (no orphans),
streaming, pipelines, timeouts, and supervision.

> **Status: pre-1.0, API in progress.** ProcessKit is an F# port of the Rust crate
> `ProcessKit-rs` and is being built feature by feature. The public API is not yet frozen —
> expect additions until the 1.0 release. See `CHANGELOG.md` for what has landed.

## Install

```bash
dotnet add package ProcessKit
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
  `Retry`.
- **Shell-free pipelines** — `Command.Pipe` with pipefail semantics and `UncheckedInPipe`.
- **Testable** — `ProcessKit.Testing.ScriptedRunner` is a subprocess-free `IProcessRunner`
  for hermetic tests.

## License

MIT
