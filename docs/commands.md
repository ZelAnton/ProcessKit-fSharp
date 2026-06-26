# Running commands

[‹ docs index](README.md)

`Command` is the entry point of the runner layer: an immutable builder that
describes *what* to run and *how*, plus a family of consuming verbs that decide
*what you get back*. Every one-shot verb spawns the child into a fresh, private
kill-on-dispose [process group](process-groups.md), so an early return, an
exception, or a dropped task can never leak a process tree.

Two equivalent surfaces build the same value: the pipe-friendly module functions
(`Command.create "git" |> Command.arg "log"`, camelCase) and the instance methods
(`(Command "git").Arg "log"`, PascalCase). They mirror each other one-for-one;
pick whichever reads better. The consuming verbs (`Run`, `OutputString`, …) are
instance methods that return `Task<Result<_, ProcessError>>`, so the F# samples
below run inside a `task { }` block and use `match!`. Where a snippet writes
`let! r = cmd.Verb()`, `r` is the `Result<_, ProcessError>` you then match. From
C# the same surface is `await`-able fluent methods. Samples assume
`open ProcessKit` and `open System`.

- [Program, arguments, working directory](#program-arguments-working-directory)
- [Environment](#environment)
- [Standard input](#standard-input)
- [Output handling](#output-handling)
- [Timeouts and retries](#timeouts-and-retries)
- [Spawn flags](#spawn-flags)
- [Consuming verbs](#consuming-verbs)
- [Results](#results)
- [Errors](#errors)

## Program, arguments, working directory

```fsharp
open ProcessKit
open System

task {
    let cmd =
        Command.create "git"
        |> Command.arg "log" // one at a time…
        |> Command.args [ "--oneline"; "-n"; "10" ] // …or in bulk
        |> Command.currentDir "/path/to/repo" // run there

    match! cmd.Run() with
    | Ok out -> printfn $"{out}"
    | Error err -> eprintfn $"{err.Message}"
}
```

The same chain in method style — identical from C#:

```fsharp
let cmd =
    (Command "git")
        .Arg("log")
        .Args([ "--oneline"; "-n"; "10" ])
        .CurrentDir("/path/to/repo")
```

Arguments are passed as a list — there is **no shell** between you and the child,
so there is no quoting, no word-splitting, and no injection surface. (When you
actually want `a | b | c`, use a [pipeline](pipelines.md), which connects the
stages in-process instead of invoking a shell.)

The program name reaches the OS verbatim: a bare name is resolved on `PATH` by
the OS, and setting a working directory does **not** re-anchor a *relative*
program path against it (a relative path resolves against the current platform's
rules — on Windows the parent's directory may win). Pass an absolute program
path when you combine a relative tool with `currentDir`.

For one-liners the top-level helpers skip the builder entirely:

```fsharp
task {
    let! version = Exec.run "dotnet" [ "--version" ] // trimmed stdout, success required
    let! status = Exec.outputString "git" [ "status"; "-s" ] // full ProcessResult
    ()
}
```

## Environment

Three builders compose and are applied at spawn:

```fsharp
open ProcessKit

task {
    // Set one variable, unset one inherited variable.
    let! _ =
        (Command.create "worker"
         |> Command.env "DOTNET_ENVIRONMENT" "Production"
         |> Command.envRemove "GIT_DIR")
            .Run()

    // Scorched earth: the child starts with an empty environment.
    let! _ = (Command.create "hermetic-tool" |> Command.envClear).Run()
    ()
}
```

- `Env key value` sets a variable for the child.
- `EnvRemove key` drops a variable the child would otherwise inherit.
- `EnvClear` starts the child from an empty environment instead of inheriting the
  parent's; any `Env` / `EnvRemove` you add still apply on top.

There is **no allow-list / inherit-subset mode**. To run with a deliberately
minimal environment, `EnvClear` and then add back only what the child needs with
`Env` — that keeps the set explicit and visible at the call site. Environment
*values* are treated as secrets by the rest of the library: they are never logged
and never written to a record/replay cassette (only the variable names are).

## Standard input

By default a child gets **no** standard input — it reads end-of-file at once and
can never hang waiting for input. Everything else is opt-in via `Stdin`:

| Source | Reusable on re-run? | Use for |
|---|---|---|
| `Stdin.Empty` | n/a (no input) | The default, made explicit |
| `Stdin.FromString "…"` | yes | Text payloads (encoded as UTF-8) |
| `Stdin.FromBytes bytes` | yes | Binary payloads |
| `Stdin.FromFile path` | yes (re-opened per run) | Large inputs streamed from disk |
| `Stdin.FromLines seq` | one-shot | A sequence of lines, each written `\n`-terminated |
| `Stdin.FromReader stream` | one-shot | Any readable `Stream` — a socket, a decompressor, … |
| `Stdin.FromAsyncLines asyncSeq` | one-shot | An `IAsyncEnumerable<string>` — a channel, a tail, … |

```fsharp
open ProcessKit

task {
    let sorted =
        Command.create "sort"
        |> Command.stdin (Stdin.FromLines [ "banana"; "apple"; "cherry" ])

    match! sorted.Run() with
    | Ok out -> printfn $"{out}" // apple / banana / cherry
    | Error err -> eprintfn $"{err.Message}"
}
```

The payload is written on a background task — so a large input can't deadlock
against the child's own output — and the pipe is closed (EOF) once the source is
exhausted, unless you also set [`KeepStdinOpen`](#spawn-flags).

The two **in-memory** sources (`FromString` / `FromBytes`) and `FromFile` are
safe to send again: a retried command (or a record/replay match) re-sends the
identical bytes, and `FromFile` is re-opened each run. The three **streaming**
sources (`FromLines` / `FromReader` / `FromAsyncLines`) wrap a live stream or
sequence that the first run drains, so they are one-shot — prefer a reusable
source whenever a command may run more than once (under [`Retry`](#timeouts-and-retries)
or [record/replay](testing.md)).

For conversational, request/response stdin — write a line, read the answer,
repeat — use `KeepStdinOpen` with the streaming API instead: see
[Streaming & interactive I/O](streaming.md).

## Output handling

### Stream modes

Each stream is connected through a `StdioMode`, set with `Command.stdout` /
`Command.stderr`. The default is `StdioMode.Piped` — required for capture, line
streaming, and per-line handlers to see anything. `StdioMode.Inherit` lets the
child share the parent's stream (its output goes straight to your terminal and
can't be captured); `StdioMode.Null` discards the stream without tying up a pipe.

### Encodings

Captured output is decoded line by line, **UTF-8 by default**. Invalid bytes
become the replacement character `U+FFFD` rather than raising an error. Override
per stream with `StdoutEncoding` / `StderrEncoding`, or both at once with
`Encoding` — each takes a `System.Text.Encoding`:

```fsharp
open ProcessKit

task {
    let cmd =
        Command.create "legacy-tool"
        |> Command.encoding System.Text.Encoding.Latin1 // both streams…
    // |> Command.stdoutEncoding enc / |> Command.stderrEncoding enc  // …or each its own

    match! cmd.OutputString() with
    | Ok result -> printfn $"{result.Stdout}"
    | Error err -> eprintfn $"{err.Message}"
}
```

A single persistent decoder runs over the whole stream, so a multi-byte sequence
that straddles two reads still decodes correctly and a `0x0A` byte inside a wider
code unit isn't mistaken for a line break. A leading byte-order mark of the chosen
encoding is stripped once, from the **decoded text only** — `OutputBytes` and the
raw [tee](#line-handlers-and-tees) stay byte-exact.

### Buffer policies — bounding memory on chatty children

Captured lines are held in memory; a multi-gigabyte log would otherwise grow the
buffer to match. `OutputBuffer` bounds *retention* — the pipe is always fully
drained, so the child never blocks — and the line counters keep counting every
line, so a count larger than what you got back reveals that lines were dropped
(and `ProcessResult.Truncated` is set):

```fsharp
open ProcessKit

// Keep the newest 1000 lines (a rolling tail; the default overflow is DropOldest):
let tail =
    Command.create "verbose-build"
    |> Command.outputBuffer (OutputBufferPolicy.Bounded 1000)

// …or freeze the head instead, keeping the first lines and dropping new ones:
let head =
    Command.create "verbose-build"
    |> Command.outputBuffer ((OutputBufferPolicy.Bounded 1000).WithOverflow OverflowMode.DropNewest)
```

`OverflowMode.DropOldest` (the default) keeps a rolling tail; `DropNewest` freezes
the head; `OverflowMode.Error` makes the ceiling **fail loud** instead of dropping.
`OutputBufferPolicy.Bounded 0` retains nothing — useful when a
[line handler](#line-handlers-and-tees) is the real consumer. `Unbounded`
(the `Default`) retains everything.

A line cap alone doesn't bound memory — one enormous newline-free "line" is held
whole. Add `WithMaxBytes` to cap the retained bytes too (either ceiling, or both):

```fsharp
// An 8 MiB retained-byte ring on an otherwise unbounded buffer:
let ring =
    Command.create "flood"
    |> Command.outputBuffer (OutputBufferPolicy.Unbounded.WithMaxBytes(8 * 1024 * 1024))

// Error if either ceiling is crossed:
let strict =
    Command.create "flood"
    |> Command.outputBuffer ((OutputBufferPolicy.FailLoud 10000).WithMaxBytes(8 * 1024 * 1024))
```

`FailLoud` (and any policy with `OverflowMode.Error`) fails the run with
`ProcessError.OutputTooLarge` once the cumulative output crosses the line or byte
cap — even while a streaming consumer is draining lines as they arrive. It bounds
memory, not wall-time, so pair it with a [`Timeout`](#timeouts-and-retries)
against a flooding child.

### Line handlers and tees

`OnStdoutLine` / `OnStderrLine` run a callback on each decoded line *in addition
to* capture or streaming — logging, progress bars, metrics. The callback runs
synchronously on the read pump as each line arrives, so keep it cheap:

```fsharp
open ProcessKit

task {
    let cmd =
        Command.create "dotnet"
        |> Command.args [ "build"; "-c"; "Release" ]
        |> Command.onStderrLine (fun line -> eprintfn $"[build] {line}")

    match! cmd.OutputString() with
    | Ok result -> printfn $"build exited {result.Code}"
    | Error err -> eprintfn $"{err.Message}"
}
```

For a ready-made copy to a `System.IO.Stream` sink — a file, a socket, anything —
reach for `StdoutTee` / `StderrTee`. Each tee copies the stream's **raw bytes**
to the sink as they are read (byte-exact: no decoding, no added newline), in
addition to capture, and runs independently of the line handlers — set both and
both fire:

```fsharp
open ProcessKit

task {
    use logFile = System.IO.File.Create "build.log"

    let cmd =
        Command.create "dotnet"
        |> Command.args [ "build" ]
        |> Command.stdoutTee logFile

    let! _ = cmd.OutputString()
    ()
}
```

## Timeouts and retries

```fsharp
open ProcessKit
open System

task {
    let cmd =
        Command.create "flaky-network-tool"
        |> Command.timeout (TimeSpan.FromSeconds 30.0) // kill the tree at the deadline
        |> Command.retry 3 (TimeSpan.FromMilliseconds 200.0) ProcessError.isTransient

    match! cmd.Run() with
    | Ok out -> printfn $"{out}"
    | Error err -> eprintfn $"{err.Message}"
}
```

- **`Timeout`** kills the whole process tree at the deadline. On the capturing
  verbs the expiry is *captured* (`ProcessResult.IsTimedOut`, `Outcome.TimedOut`);
  on the success-checking verbs it *raises* `ProcessError.Timeout`. The full
  decision table lives in
  [Timeouts, retries & cancellation](timeouts-and-cancellation.md).
- **`TimeoutGrace`** softens the kill: on timeout it terminates gracefully
  (SIGTERM), waits the grace window, then force-kills only if the child is still
  alive. On Windows this degrades to the atomic Job-object kill.
- **`Retry`** re-runs the command up to `maxAttempts` *additional* times after the
  first, waiting `delay` between attempts, while your classifier returns `true`
  for the error (`ProcessError.isTransient` covers spawn races and I/O blips). The
  classifier sees the typed `ProcessError`; a cancelled token stops the loop.

To tie a run to a `CancellationToken`, use `CancelOn` (or pass a token to any verb
overload, `cmd.Run(ct)`). A cancelled run is **always** an error
(`ProcessError.Cancelled`), never a captured outcome — see
[Timeouts, retries & cancellation](timeouts-and-cancellation.md).

## Spawn flags

```fsharp
open ProcessKit

task {
    // Windows: no console window flashes up from a GUI app (a harmless no-op elsewhere).
    let! _ = (Command.create "helper" |> Command.createNoWindow).Run()
    ()
}
```

- **`CreateNoWindow`** runs a console child with `CREATE_NO_WINDOW` on Windows, so
  a tool spawned from a GUI app doesn't flash a console window. No effect on Unix.
- **`KeepStdinOpen`** keeps the child's stdin pipe open after its source is
  exhausted (or with no source at all), so you can write to it interactively via
  `RunningProcess.TakeStdin` — see [Streaming & interactive I/O](streaming.md).

ProcessKit wires **pipes**, not a pseudo-terminal, so a tool that *demands* a tty
— an `ssh` / `sudo` **password** prompt, some credential helpers — won't get one.
Drive such tools non-interactively instead (key-based auth, `ssh -o BatchMode=yes`,
`GIT_TERMINAL_PROMPT=0`), or feed a known answer over
[interactive stdin](streaming.md).

## Consuming verbs

The builder describes the run; the verb you finish with decides what you get back.
Every verb returns `Task<Result<_, ProcessError>>`, and every verb has a
`CancellationToken` overload (`cmd.Run(ct)`).

| Verb | `Ok` payload | Non-zero exit | Use when |
|---|---|---|---|
| `OutputString()` | `ProcessResult<string>` | captured (data) | You want to inspect the outcome yourself |
| `OutputBytes()` | `ProcessResult<byte[]>` | captured (data) | Binary stdout (images, archives, …) |
| `Run()` | trimmed `string` | `ProcessError.Exit` | "Give me the answer or fail" |
| `RunUnit()` | `unit` | `ProcessError.Exit` | You only care that it succeeded |
| `ExitCode()` | `int` | the code, as `Ok` | The code *is* the answer |
| `Probe()` | `bool` | `0`→`true`, `1`→`false`, else error | Predicate commands: `git diff --quiet`, `grep -q` |
| `Parse(f)` / `TryParse(f)` | `'T` | `ProcessError.Exit` | A typed value from stdout (success required) |
| `FirstLine(p)` | `string option` | — (stream-based) | Grab one matching line, kill the rest |
| `Start()` | `RunningProcess` | — | A live handle: [streaming, stdin, probes](streaming.md) |

`Run` returns stdout with trailing whitespace trimmed. `ExitCode` hands back a
non-zero exit as `Ok` data, but a signal kill or timeout errors rather than
inventing a sentinel like `-1`. `Probe` errors on any exit other than `0` or `1`.
`Parse` maps the trimmed stdout through `f` (a thrown parser becomes
`ProcessError.Parse`); `TryParse` lets `f` return its own `Result<'T, string>`
whose error message becomes `ProcessError.Parse`. `FirstLine` returns the first
stdout line matching the predicate and kills the (private-group) child the moment
it has its answer — you never wait out a long log for one line — and returns
`Ok None` when stdout closes without a match.

```fsharp
open ProcessKit

task {
    // Probe: the exit code as a yes/no.
    match! (Command.create "git" |> Command.args [ "diff"; "--quiet" ]).Probe() with
    | Ok true -> printfn "working tree clean"
    | Ok false -> printfn "there are changes"
    | Error err -> eprintfn $"{err.Message}"

    // Parse: a typed value from stdout.
    let! version = (Command.create "node" |> Command.arg "--version").Parse(fun s -> s.TrimStart('v'))

    // FirstLine: stop as soon as the interesting line appears.
    match! (Command.create "git" |> Command.args [ "log"; "--oneline" ]).FirstLine(fun l -> l.Contains "fix:") with
    | Ok(Some line) -> printfn $"{line}"
    | Ok None -> printfn "no fix commit"
    | Error err -> eprintfn $"{err.Message}"
}
```

The same vocabulary repeats on every layer. To run a verb through a specific
`IProcessRunner` — the dependency-injection and test seam — go through the
`Runner` module (`Runner.run runner CancellationToken.None cmd`); the verbs also
exist on [`CliClient`](testing.md), [`Pipeline`](pipelines.md), and as the
`Exec.*` one-liners.

## Results

The capturing verbs (`OutputString` / `OutputBytes`) hand back a
`ProcessResult<'T>` — a non-zero exit is **data** here, not an error:

```fsharp
open ProcessKit

task {
    match! (Command.create "git" |> Command.args [ "merge"; "feature" ]).OutputString() with
    | Ok result ->
        printfn $"code={result.Code} success={result.IsSuccess} timedOut={result.IsTimedOut}"
        printfn $"took {result.Duration}, truncated={result.Truncated}"

        // Opt into erroring whenever you're ready:
        match ProcessResult.ensureSuccess result with
        | Ok ok -> printfn $"{ok.Stdout}"
        | Error err -> eprintfn $"{err.Message}"
    | Error err -> eprintfn $"{err.Message}"
}
```

The accessors:

| Member | Meaning |
|---|---|
| `Stdout` | Captured stdout — `string` (text verbs) or `byte[]` (bytes verbs) |
| `Stderr` | Captured stderr, as decoded text |
| `Code` | The exit code, or `None` for a signal kill / timeout |
| `Signal` | The terminating signal number (Unix), else `None` |
| `IsSuccess` | The code is in `AcceptedCodes` (`{0}` by default) |
| `IsTimedOut` | The run's own deadline expired |
| `Outcome` | The three-way enum behind the accessors above |
| `Duration` | Wall-clock duration of the run |
| `Truncated` | A buffer policy dropped output |
| `AcceptedCodes` | The exit codes treated as success (`{0}` plus any `OkCodes`) |

`ProcessResult.ensureSuccess` converts a `ProcessResult<string>` into a
`Result` — the result unchanged on success, otherwise the matching
`ProcessError` (`Exit` / `Signalled` / `Timeout`).

### Accepting non-zero exits

Some tools use a non-zero exit as information (`grep` returns `1` for "no match").
Tell ProcessKit which codes count as success with `OkCodes`:

```fsharp
open ProcessKit

task {
    let grep =
        Command.create "grep"
        |> Command.args [ "ERROR"; "app.log" ]
        |> Command.okCodes [ 0; 1 ] // 1 ("no match") is success, not failure

    match! grep.Run() with
    | Ok output -> printfn $"matches:\n{output}"
    | Error err -> eprintfn $"{err.Message}" // a real failure (e.g. exit 2)
}
```

`OkCodes` widens `ProcessResult.IsSuccess`, `ensureSuccess`, and `Run` / `RunUnit`.
An empty set resets to the default `{0}`.

### The `Outcome` enum

When the three-way distinction matters, match on `Outcome` instead of decoding the
`Code` / `IsTimedOut` pair:

```fsharp
open ProcessKit

match result.Outcome with
| Outcome.Exited 0 -> printfn "clean"
| Outcome.Exited code -> printfn $"failed with {code}"
| Outcome.Signalled signal -> printfn $"killed by signal {signal}"
| Outcome.TimedOut -> printfn "hit its deadline"
```

`Outcome` carries the same `Code` / `Signal` / `IsTimedOut` accessors as
`ProcessResult`, so a bare `Outcome` (from `RunningProcess.Wait` or
`Finished.Outcome`) answers directly. There is no success accessor on `Outcome` —
success is `OkCodes`-aware, so use `ProcessResult.IsSuccess`.

## Errors

`ProcessError` is a discriminated union: pattern-match it, read `.Message` for a
one-line description (it is also the `ToString()`), or use the classifiers. The
capturing verbs only error on a *failure to run* (spawn / not-found / I/O /
timeout / cancellation) — never on a non-zero exit; the success-checking verbs
(`Run` / `RunUnit` / `Parse` / `TryParse`) additionally turn a non-zero exit into
`ProcessError.Exit`.

```fsharp
open ProcessKit

task {
    match! (Command.create "deploy").Run() with
    | Ok out -> printfn $"{out}"
    | Error(ProcessError.NotFound(program, _)) -> eprintfn $"not installed: {program}"
    | Error(ProcessError.Exit(program, code, _, stderr)) -> eprintfn $"{program} exited {code}: {stderr}"
    | Error(ProcessError.Timeout(program, t, _, _)) -> eprintfn $"{program} timed out after {t}"
    | Error err -> eprintfn $"{err.Message}"
}
```

| Variant | Fields | Meaning |
|---|---|---|
| `ProcessError.Spawn` | `program, message` | The program was located but the OS couldn't start it (permissions, a bad working directory, a Windows `.cmd`/`.bat` needing `cmd.exe`, …). **Not** `isNotFound`. |
| `ProcessError.NotFound` | `program, searched: string option` | The program couldn't be located (`isNotFound` is `true`); `searched` is the probed path when known. |
| `ProcessError.Exit` | `program, code, stdout, stderr` | A success-requiring verb saw a non-zero exit; both streams attached in full. |
| `ProcessError.Signalled` | `program, signal: int option, stdout, stderr` | Killed by a signal with no exit code; `signal` carries the number on Unix, `None` elsewhere; the partial streams captured before the kill are attached. |
| `ProcessError.Timeout` | `program, timeout, stdout, stderr` | The run's own deadline killed it; whatever it captured before the kill is attached. |
| `ProcessError.NotReady` | `program, timeout` | A [readiness probe](streaming.md) gave up — distinct from a timeout. |
| `ProcessError.Parse` | `program, message` | A `Parse` / `TryParse` parser rejected the output. |
| `ProcessError.OutputTooLarge` | `program, lineLimit, byteLimit, totalLines, totalBytes` | A `FailLoud` (`OverflowMode.Error`) buffer ceiling was exceeded. |
| `ProcessError.Stdin` | `program, message` | Feeding the child's stdin failed for a non-broken-pipe reason on an otherwise-successful run (a louder exit/signal/timeout failure wins instead). |
| `ProcessError.CassetteMiss` | `program` | A record/replay cassette found no matching recording — kept distinct from not-found, so `isNotFound` is `false`. |
| `ProcessError.Unsupported` | `operation` | The platform can't do what was asked (e.g. a POSIX signal on Windows) and silently skipping would be wrong. |
| `ProcessError.Cancelled` | `program` | The run's `CancellationToken` fired. Always an error. |
| `ProcessError.ResourceLimit` | `message` | A requested [resource cap](process-groups.md) couldn't be enforced. |
| `ProcessError.Io` | `message` | A low-level I/O failure from ProcessKit's own machinery (driving a child, group control, cassette files). |

Two classifiers help with retry and diagnostic logic:

```fsharp
match! cmd.Run() with
| Ok _ -> ()
| Error err when ProcessError.isNotFound err -> installThenRetry () // NotFound only
| Error err when ProcessError.isTransient err -> scheduleRetry () // Spawn / Io blips
| Error err -> fail err
```

`ProcessError.isNotFound` is `true` only for `NotFound`; `ProcessError.isTransient`
is `true` for `Spawn` and `Io` — failures that may succeed on a retry.

---

Next: [Streaming & interactive I/O](streaming.md) ·
[Timeouts, retries & cancellation](timeouts-and-cancellation.md) ·
[Process groups](process-groups.md)
