# Coming from ProcessKit-rs

[Previous: Comparison and migration guide](comparison.md)

ProcessKit for .NET and `ProcessKit-rs` share the same contract: async process
execution, typed failures, honest non-zero outcomes, whole-tree containment, and
an injectable runner seam. The spelling follows each ecosystem. This guide maps
the concepts without pretending that Rust ownership and .NET disposal are the
same mechanism.

## Verb map

| ProcessKit-rs | ProcessKit for .NET | Result |
|---|---|---|
| `Command::run().await` | `Command.RunAsync()` | accepted exit required; trimmed stdout |
| `Command::output_string().await` | `Command.OutputStringAsync()` | full `ProcessResult<string>`; non-zero exit remains data |
| `Command::output_bytes().await` | `Command.OutputBytesAsync()` | full `ProcessResult<byte[]>` |
| `Command::exit_code().await` | `Command.ExitCodeAsync()` | exit code; a timeout or signal is an error |
| `Command::probe().await` | `Command.ProbeAsync()` | `0 → true`, `1 → false`, any other outcome is an error |
| `Command::start().await` | `Command.StartAsync()` | live process for streaming, stdin, and readiness waits |

The most important distinction survives the language change: `output_string` /
`OutputStringAsync` captures a non-zero exit in the result, while `run` /
`RunAsync` promotes an unaccepted exit to a typed error.

The Rust blocks on this page are non-compiled illustrations; the repository's
snippet harness compiles every paired F# and C# block against the current .NET API.

```rust
let output = Command::new("git")
    .arg("status")
    .output_string()
    .await?;
println!("code={:?} {}", output.code(), output.stdout());
```

**F#**

```fsharp
task {
    let command = Command.create "git" |> Command.arg "status"

    match! command.OutputStringAsync() with
    | Ok output -> printfn $"code={output.Code} {output.Stdout}"
    | Error err -> eprintfn $"{err.Message}"
}
```

**C#**

```csharp
var command = new Command("git").Arg("status");

Console.WriteLine(await command.OutputStringAsync() switch
{
    { IsOk: true, ResultValue: var output } => $"code={output.Code} {output.Stdout}",
    { IsOk: false, ErrorValue: var err }    => $"error: {err.Message}",
});
```

## Ownership and teardown

Rust makes teardown visible through ownership: dropping a `RunningProcess` or
`ProcessGroup` reaps its contained tree. .NET cannot attach correctness to the GC
lifetime, so the same deterministic boundary is `IDisposable` /
`IAsyncDisposable`. Bind the live handle with `use` in F# or `using` /
`await using` in C#. F# `use!` is the corresponding spelling when an async
factory returns the disposable directly; ProcessKit's built-in `StartAsync`
returns an honest `Result`, so match it first and then bind the successful handle
with `use`, as below. Do not leave containment to a finalizer.

```rust
let process = Command::new("server").start().await?;
// Dropping `process` tears down its private contained tree.
```

**F#**

```fsharp
task {
    match! (Command.create "server").StartAsync() with
    | Error err -> eprintfn $"{err.Message}"
    | Ok process ->
        use process = process
        match! process.WaitForLineAsync((fun line -> line.Contains "ready"), TimeSpan.FromSeconds 10.0) with
        | Ok _ -> printfn "ready"
        | Error err -> eprintfn $"{err.Message}"
}
```

## Errors and honest outcomes

| Rust vocabulary | .NET vocabulary |
|---|---|
| `Result<T, processkit::Error>` | `Task<Result<'T, ProcessError>>` in F#; `Task<FSharpResult<T, ProcessError>>` in C# |
| `Error::reason()` / `ErrorReason` | pattern-match the `ProcessError` discriminated union |
| `ProcessResult<T>` | `ProcessResult<'T>` with `Outcome`, `Code`, `Stdout`, and `Stderr` |
| `ErrorReason::Unsupported` | `ProcessError.Unsupported` |
| `ErrorReason::NotFound` | `ProcessError.NotFound` |
| `ErrorReason::Cancelled` | `ProcessError.Cancelled` |

Both libraries keep a non-zero exit as data for capture verbs and make
cancellation an error. Match the structured case; do not parse `Message`.

## Cancellation

Rust's `Command::cancel_on(CancellationToken)` maps directly to
`Command.CancelOn(CancellationToken)` / `Command.cancelOn`. Every consuming .NET
verb also accepts a call-scoped `CancellationToken`; `CancelOn` is useful when a
preconfigured command or `CliClient` carries its own lifetime.

```rust
let output = Command::new("worker")
    .cancel_on(shutdown.child_token())
    .output_string()
    .await?;
```

**F#**

```fsharp
task {
    use shutdown = new CancellationTokenSource(TimeSpan.FromSeconds 30.0)

    let command =
        Command.create "worker"
        |> Command.cancelOn shutdown.Token

    let! result = command.OutputStringAsync()
    return result
}
```

## Features become packages or always-available modules

Rust uses Cargo features to keep optional dependencies and platform surfaces out
of a build. NuGet has no equivalent compile-time feature gate, so the core .NET
package exposes production capabilities directly and isolates optional concerns
in side packages.

| ProcessKit-rs feature/concept | ProcessKit for .NET |
|---|---|
| default process control, `stats`, `limits`, `record`, `pty` | core modules are available without feature flags; record/replay lives in `ProcessKit.Testing` |
| `tracing` | optional `ILogger`; `ProcessKitDiagnostics.ActivitySource` for traces |
| `metrics` | `ProcessKitDiagnostics.Meter` |
| `mock` / `ProcessRunner` | `IProcessRunner` plus `ScriptedRunner`, `FakeProcess`, and `RecordReplayRunner` in `ProcessKit.Testing` |
| application DI wiring | `ProcessKit.Extensions.DependencyInjection` |
| hosted supervision | `ProcessKit.Extensions.Hosting` |

The core remains DI-friendly rather than DI-coupled: production code can accept
the plain `IProcessRunner` interface without referencing a container package.

## Encoding, PTY, and supervision

| Concern | Rust | .NET |
|---|---|---|
| Text encoding | per-stream encoding configuration, with raw byte capture when text is the wrong abstraction | `Command.Encoding`, `StdoutEncoding`, `StderrEncoding`, `StdinEncoding`, or `OutputBytesAsync` |
| PTY | Cargo `pty` feature plus `Command::use_pty` | `Command.Pty(PtyConfig)` and `PtySession`; unsupported platform details are typed |
| Streaming | `RunningProcess` streams and waits | `RunningProcess` exposes `IAsyncEnumerable` lines/events and readiness waits |
| Supervision | `Supervisor` restart/backoff policy | `Supervisor` in the core package; hosted lifetime wiring in `ProcessKit.Extensions.Hosting` |
| Observability | `tracing` and `metrics` feature adapters | `ILogger`, `ActivitySource`, and `Meter`, all secret-safe by contract |

The names differ, but the design test remains the same: select a capability
explicitly, inspect a typed unsupported result when the platform cannot provide
it, and keep the process tree owned by a deterministic lifetime.

## Testing seam

Code that accepts Rust's `&dyn ProcessRunner` should usually accept .NET's
`IProcessRunner`. In tests, `ScriptedRunner` is the stable default on both sides;
the .NET version additionally provides `FakeProcess` for live streaming and
`RecordReplayRunner` for cassettes in the same `ProcessKit.Testing` package.

**C#**

```csharp
IProcessRunner runner =
    new ScriptedRunner().On(["git", "status"], Reply.Ok("clean"));

var result = await runner.RunAsync(new Command("git").Arg("status"));
Console.WriteLine(result);
```

See [Testing your code](testing.md) for structural invocation journals, streaming
doubles, and cassette matching.

---

Next: [Cookbook](cookbook.md)
