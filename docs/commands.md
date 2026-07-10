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
pick whichever reads better. The consuming verbs (`RunAsync`, `OutputStringAsync`, …) are
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

**F#**

```fsharp
task {
    let cmd =
        Command.create "git"
        |> Command.arg "log" // one at a time…
        |> Command.args [ "--oneline"; "-n"; "10" ] // …or in bulk
        |> Command.currentDir "/path/to/repo" // run there

    match! cmd.RunAsync() with
    | Ok out -> printfn $"{out}"
    | Error err -> eprintfn $"{err.Message}"
}
```

**C#**

```csharp
var cmd =
    new Command("git")
        .Arg("log") // one at a time…
        .Args(["--oneline", "-n", "10"]) // …or in bulk
        .CurrentDir("/path/to/repo"); // run there

Console.WriteLine(await cmd.RunAsync() switch
{
    { IsOk: true, ResultValue: var output } => output,
    { IsOk: false, ErrorValue: var err }   => err.Message,
});
```

The same chain in method style — identical from C#:

**F#**

```fsharp
let cmd =
    (Command "git")
        .Arg("log")
        .Args([ "--oneline"; "-n"; "10" ])
        .CurrentDir("/path/to/repo")
```

**C#**

```csharp
var cmd =
    new Command("git")
        .Arg("log")
        .Args(["--oneline", "-n", "10"])
        .CurrentDir("/path/to/repo");
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

**F#**

```fsharp
task {
    let! version = Exec.run "dotnet" [ "--version" ] // trimmed stdout, success required
    let! status = Exec.outputString "git" [ "status"; "-s" ] // full ProcessResult
    ()
}
```

**C#**

```csharp
var version = await Exec.run("dotnet", ["--version"]); // trimmed stdout, success required
var status = await Exec.outputString("git", ["status", "-s"]); // full ProcessResult
```

## Environment

Three builders compose and are applied at spawn:

**F#**

```fsharp
task {
    // Set one variable, unset one inherited variable.
    let! _ =
        (Command.create "worker"
         |> Command.env "DOTNET_ENVIRONMENT" "Production"
         |> Command.envRemove "GIT_DIR")
            .RunAsync()

    // Scorched earth: the child starts with an empty environment.
    let! _ = (Command.create "hermetic-tool" |> Command.envClear).RunAsync()
    ()
}
```

**C#**

```csharp
// Set one variable, unset one inherited variable.
await new Command("worker")
    .Env("DOTNET_ENVIRONMENT", "Production")
    .EnvRemove("GIT_DIR")
    .RunAsync();

// Scorched earth: the child starts with an empty environment.
await new Command("hermetic-tool").EnvClear().RunAsync();
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
| `Stdin.FromStream stream` | one-shot | Any readable `Stream` — a socket, a decompressor, … |
| `Stdin.FromAsyncLines asyncSeq` | one-shot | An `IAsyncEnumerable<string>` — a channel, a tail, … |

**F#**

```fsharp
task {
    let sorted =
        Command.create "sort"
        |> Command.stdin (Stdin.FromLines [ "banana"; "apple"; "cherry" ])

    match! sorted.RunAsync() with
    | Ok out -> printfn $"{out}" // apple / banana / cherry
    | Error err -> eprintfn $"{err.Message}"
}
```

**C#**

```csharp
var sorted =
    new Command("sort")
        .Stdin(Stdin.FromLines(["banana", "apple", "cherry"]));

Console.WriteLine(await sorted.RunAsync() switch
{
    { IsOk: true, ResultValue: var output } => output, // apple / banana / cherry
    { IsOk: false, ErrorValue: var err }   => err.Message,
});
```

The payload is written on a background task — so a large input can't deadlock
against the child's own output — and the pipe is closed (EOF) once the source is
exhausted, unless you also set [`KeepStdinOpen`](#spawn-flags).

The two **in-memory** sources (`FromString` / `FromBytes`) and `FromFile` are
safe to send again: a retried command (or a record/replay match) re-sends the
identical bytes, and `FromFile` is re-opened each run. The three **streaming**
sources (`FromLines` / `FromStream` / `FromAsyncLines`) wrap a live stream or
sequence that the first run drains, so they are one-shot — prefer a reusable
source whenever a command may run more than once (under [`Retry`](#timeouts-and-retries)
or [record/replay](testing.md)).

For conversational, request/response stdin — write a line, read the answer,
repeat — use `KeepStdinOpen` with the streaming API instead: see
[Streaming & interactive I/O](streaming.md).

## Output handling

### Stream modes

Each stream is connected through a `StdioMode`, set with `Command.Stdout` /
`Command.Stderr`. The default is `StdioMode.Piped` — required for capture, line
streaming, and per-line handlers to see anything. `StdioMode.Inherit` lets the
child share the parent's stream (its output goes straight to your terminal and
can't be captured); `StdioMode.Null` discards the stream without tying up a pipe.

### Merging stderr into stdout (`2>&1`)

`Command.MergeStderr` folds the child's standard error into its standard output
**at the OS level** — the library equivalent of a shell `2>&1`. The native spawn
points the child's stderr at the very same pipe/handle as its stdout (a POSIX
`dup2` of fd 2 onto stdout's target; on Windows one handle shared across
`STARTUPINFO.hStdOutput`/`hStdError`), so the two streams interleave **honestly,
byte for byte** on the single stdout stream — the real terminal-order view. This
is the "log exactly as the terminal shows it" case, and it is what
`ProcessResult.Combined` (a post-hoc concatenation of the two *separately*
captured streams — stdout, then stderr) cannot give you: `Combined` never
reproduces the true interleaving, `MergeStderr` does.

**F#**

```fsharp
task {
    let cmd = Command.create "noisy-build" |> Command.mergeStderr

    match! cmd.OutputStringAsync() with
    | Ok result -> printfn $"{result.Stdout}" // stdout + stderr, in real order
    | Error err -> eprintfn $"{err.Message}"
}
```

**C#**

```csharp
var cmd = new Command("noisy-build").MergeStderr();

Console.WriteLine(await cmd.OutputStringAsync() switch
{
    { IsOk: true, ResultValue: var result } => result.Stdout,
    { IsOk: false, ErrorValue: var err }   => err.Message,
});
```

When merging is on there is **no separate stderr stream**, and the API says so
rather than downgrading silently: `ProcessResult.Stderr` is empty, the streamed
`OutputEventsAsync` emits only `OutputEvent.Stdout` events (the stderr lines are
already interleaved into the stdout byte stream), and the separate-stderr
*observation* knobs are rejected in combination — `StderrTee` and `OnStderrLine`
throw `ArgumentException` alongside `MergeStderr`, in either chaining order.
The remaining stderr knobs are **no-ops** under merge: the merged bytes follow
stdout's settings, so `StderrEncoding` gives way to `StdoutEncoding`,
`StderrLineTerminator` to `StdoutLineTerminator`, and the `Stderr` `StdioMode` to
stdout's destination. Inside a [pipeline](pipelines.md) `MergeStderr` is allowed
only on the last stage.

### Encodings

Captured output is decoded line by line, **UTF-8 by default**. Invalid bytes
become the replacement character `U+FFFD` rather than raising an error. Override
per stream with `StdoutEncoding` / `StderrEncoding`, or both at once with
`Encoding` — each takes a `System.Text.Encoding`:

**F#**

```fsharp
task {
    let cmd =
        Command.create "legacy-tool"
        |> Command.encoding System.Text.Encoding.Latin1 // both streams…
    // |> Command.stdoutEncoding enc / |> Command.stderrEncoding enc  // …or each its own

    match! cmd.OutputStringAsync() with
    | Ok result -> printfn $"{result.Stdout}"
    | Error err -> eprintfn $"{err.Message}"
}
```

**C#**

```csharp
var cmd =
    new Command("legacy-tool")
        .Encoding(System.Text.Encoding.Latin1); // both streams…
// .StdoutEncoding(enc) / .StderrEncoding(enc)  // …or each its own

Console.WriteLine(await cmd.OutputStringAsync() switch
{
    { IsOk: true, ResultValue: var result } => result.Stdout,
    { IsOk: false, ErrorValue: var err }   => err.Message,
});
```

A single persistent decoder runs over the whole stream, so a multi-byte sequence
that straddles two reads still decodes correctly and a `0x0A` byte inside a wider
code unit isn't mistaken for a line break. The decoder is finalized at EOF, so an
incomplete trailing sequence follows the encoding's configured decoder fallback:
the default emits `U+FFFD`, while `DecoderExceptionFallback` raises its decoding
exception. A leading byte-order mark of the chosen
encoding is stripped once, from the **decoded text only** — `OutputBytesAsync` and the
raw [tee](#line-handlers-and-tees) stay byte-exact.

### Buffer policies — bounding memory on chatty children

Captured lines are held in memory; a multi-gigabyte log would otherwise grow the
buffer to match. `OutputBuffer` bounds *retention* — the pipe is always fully
drained, so the child never blocks — and the line counters keep counting every
line, so a count larger than what you got back reveals that lines were dropped
(and `ProcessResult.Truncated` is set):

**F#**

```fsharp
// Keep the newest 1000 lines (a rolling tail; the default overflow is DropOldest):
let tail =
    Command.create "verbose-build"
    |> Command.outputBuffer (OutputBufferPolicy.Bounded 1000)

// …or freeze the head instead, keeping the first lines and dropping new ones:
let head =
    Command.create "verbose-build"
    |> Command.outputBuffer ((OutputBufferPolicy.Bounded 1000).WithOverflow OverflowMode.DropNewest)
```

**C#**

```csharp
// Keep the newest 1000 lines (a rolling tail; the default overflow is DropOldest):
var tail =
    new Command("verbose-build")
        .OutputBuffer(OutputBufferPolicy.Bounded(1000));

// …or freeze the head instead, keeping the first lines and dropping new ones:
var head =
    new Command("verbose-build")
        .OutputBuffer(OutputBufferPolicy.Bounded(1000).WithOverflow(OverflowMode.DropNewest));
```

`OverflowMode.DropOldest` (the default) keeps a rolling tail; `DropNewest` freezes
the head; `OverflowMode.Error` makes the ceiling **fail loud** instead of dropping.
`OutputBufferPolicy.Bounded 0` retains nothing — useful when a
[line handler](#line-handlers-and-tees) is the real consumer. `Unbounded`
(the `Default`) retains everything.

A line cap alone doesn't bound memory — without a byte cap an enormous newline-free
"line" grows whole. `WithMaxBytes` caps the retained bytes **and** the in-flight
(not-yet-terminated) line — force-flushed at the cap — so even a newline-free flood
stays bounded (set either ceiling, or both). This also covers the opposite shape: an
unbounded flood of *empty* lines (bare newlines). Each retained line counts its own
UTF-8 bytes **plus one byte** for the `\n` separator the reassembled text needs, so
even an empty line (`0` content bytes) still costs `1` toward the cap — `MaxBytes`
alone (no `MaxLines`) genuinely bounds an empty-line flood too, not just a
newline-free one:

**F#**

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

**C#**

```csharp
// An 8 MiB retained-byte ring on an otherwise unbounded buffer:
var ring =
    new Command("flood")
        .OutputBuffer(OutputBufferPolicy.Unbounded.WithMaxBytes(8 * 1024 * 1024));

// Error if either ceiling is crossed:
var strict =
    new Command("flood")
        .OutputBuffer(OutputBufferPolicy.FailLoud(10000).WithMaxBytes(8 * 1024 * 1024));
```

`FailLoud` (and any policy with `OverflowMode.Error`) fails the run with
`ProcessError.OutputTooLarge` once the cumulative output crosses the line or byte
cap — even while a streaming consumer is draining lines as they arrive. It bounds
memory, not wall-time, so pair it with a [`Timeout`](#timeouts-and-retries)
against a flooding child.

#### Raw byte captures obey the byte cap too

`OutputBytesAsync` captures stdout as raw bytes with no line structure, so only the
**byte** side of the policy applies to it — `MaxLines` is meaningless there and is
ignored. `MaxBytes = Some cap` enforces the cap per `Overflow`: `Error` returns
`ProcessError.OutputTooLarge` once the cumulative stdout exceeds `cap` (the pipe is
still drained, so the child never blocks), `DropOldest` keeps the **last** `cap`
bytes, and `DropNewest` keeps the **first** `cap` bytes — the dropping modes set
`ProcessResult.Truncated`. `MaxBytes = None` (the default) leaves the raw stdout
capture **unbounded**, exactly as before. `ProcessResult.Truncated` on a byte
capture reflects truncation of stdout *or* stderr, and `OutputTooLarge` fires if
either stream trips its fail-loud ceiling. Unlike the line-based path above, a raw
byte capture has no per-line separator surcharge — `cap` is the literal byte count,
since there is no line structure to reassemble.

```fsharp
// Keep the last 1 MiB of a binary stream; anything earlier is dropped, Truncated is set:
let tail =
    Command.create "produce-archive"
    |> Command.outputBuffer (OutputBufferPolicy.Unbounded.WithMaxBytes(1024 * 1024))

// …or refuse to buffer more than 1 MiB at all:
let strict =
    Command.create "produce-archive"
    |> Command.outputBuffer ((OutputBufferPolicy.Unbounded.WithMaxBytes(1024 * 1024)).WithOverflow OverflowMode.Error)
```

A [pipeline](pipelines.md) captures its last stage's stdout as raw bytes, so the same
byte cap + overflow of that **last** stage's `OutputBuffer` bound the pipeline's
captured output (its `MaxLines`, and every intermediate stage's policy, do not apply).

> This is a deliberate divergence from the Rust `ProcessKit-rs` reference, whose
> `output_bytes` bounds raw bytes only by `Timeout`, not by the buffer policy. The
> port applies the byte cap honestly so that a caller who set `MaxBytes`/`FailLoud`
> to bound memory is not handed an unbounded stdout buffer.

### Line handlers and tees

`OnStdoutLine` / `OnStderrLine` run a callback on each decoded line *in addition
to* capture or streaming — logging, progress bars, metrics. The callback runs
synchronously on the read pump as each line arrives, so keep it cheap:

**F#**

```fsharp
task {
    let cmd =
        Command.create "dotnet"
        |> Command.args [ "build"; "-c"; "Release" ]
        |> Command.onStderrLine (fun line -> eprintfn $"[build] {line}")

    match! cmd.OutputStringAsync() with
    | Ok result -> printfn $"build exited {result.Code}"
    | Error err -> eprintfn $"{err.Message}"
}
```

**C#**

```csharp
var cmd =
    new Command("dotnet")
        .Args(["build", "-c", "Release"])
        .OnStderrLine(line => Console.Error.WriteLine($"[build] {line}"));

Console.WriteLine(await cmd.OutputStringAsync() switch
{
    { IsOk: true, ResultValue: var result } => $"build exited {result.Code}",
    { IsOk: false, ErrorValue: var err }   => err.Message,
});
```

For a ready-made copy to a `System.IO.Stream` sink — a file, a socket, anything —
reach for `StdoutTee` / `StderrTee`. Each tee copies the stream's **raw bytes**
to the sink as they are read (byte-exact: no decoding, no added newline), in
addition to capture, and runs independently of the line handlers — set both and
both fire:

**F#**

```fsharp
task {
    use logFile = System.IO.File.Create "build.log"

    let cmd =
        Command.create "dotnet"
        |> Command.args [ "build" ]
        |> Command.stdoutTee logFile

    let! _ = cmd.OutputStringAsync()
    ()
}
```

**C#**

```csharp
using var logFile = System.IO.File.Create("build.log");

var cmd =
    new Command("dotnet")
        .Args(["build"])
        .StdoutTee(logFile);

await cmd.OutputStringAsync();
```

## Timeouts and retries

**F#**

```fsharp
task {
    let cmd =
        Command.create "flaky-network-tool"
        |> Command.timeout (TimeSpan.FromSeconds 30.0) // kill the tree at the deadline
        |> Command.retry 3 (TimeSpan.FromMilliseconds 200.0) ProcessError.isTransient

    match! cmd.RunAsync() with
    | Ok out -> printfn $"{out}"
    | Error err -> eprintfn $"{err.Message}"
}
```

**C#**

```csharp
var cmd =
    new Command("flaky-network-tool")
        .Timeout(TimeSpan.FromSeconds(30)) // kill the tree at the deadline
        .Retry(3, TimeSpan.FromMilliseconds(200), err => err.IsTransient);

Console.WriteLine(await cmd.RunAsync() switch
{
    { IsOk: true, ResultValue: var output } => output,
    { IsOk: false, ErrorValue: var err }   => err.Message,
});
```

- **`Timeout`** kills the whole process tree at the deadline. On the capturing
  verbs the expiry is *captured* (`ProcessResult.IsTimedOut`, `Outcome.TimedOut`);
  on the success-checking verbs it *raises* `ProcessError.Timeout`. The full
  decision table lives in
  [Timeouts, retries & cancellation](timeouts-and-cancellation.md).
- **`TimeoutGrace`** softens the kill: on timeout it terminates gracefully
  (SIGTERM), waits the grace window, then force-kills only if the child is still
  alive. On Windows this degrades to the atomic Job-object kill.
- **`Retry`** runs the command up to `maxAttempts` times **in total** (the first run
  plus up to `maxAttempts - 1` retries — so `retry 3` is one run and up to two
  retries, and `0`/`1` both mean a single run), waiting `delay` between attempts,
  while your classifier returns `true` for the error (`ProcessError.isTransient`
  covers spawn races and I/O blips). The classifier sees the typed `ProcessError`; a
  cancelled token stops the loop.
- **`RetryNever`** explicitly disables retrying for this command — it always runs
  exactly once. This differs from simply never calling `Retry`: a `CliClient` built
  with `WithDefaults(fun c -> c.Retry(...))` applies that default `Retry` to every
  command built from its template, and `RetryNever` is the one way to opt a specific
  command out of an inherited default. Calling `Retry` again after `RetryNever` in
  the same chain re-enables retrying — the last of the two wins, like any other
  builder call.

To tie a run to a `CancellationToken`, use `CancelOn` (or pass a token to any verb's
optional token parameter, `cmd.RunAsync(ct)`). A cancelled run is **always** an error
(`ProcessError.Cancelled`), never a captured outcome — see
[Timeouts, retries & cancellation](timeouts-and-cancellation.md).

## Spawn flags

**F#**

```fsharp
task {
    // Windows: no console window flashes up from a GUI app (a harmless no-op elsewhere).
    let! _ = (Command.create "helper" |> Command.createNoWindow).RunAsync()
    ()
}
```

**C#**

```csharp
// Windows: no console window flashes up from a GUI app (a harmless no-op elsewhere).
await new Command("helper").CreateNoWindow().RunAsync();
```

- **`CreateNoWindow`** runs a console child with `CREATE_NO_WINDOW` on Windows, so
  a tool spawned from a GUI app doesn't flash a console window. No effect on Unix.
- **`KeepStdinOpen`** keeps the child's stdin pipe open after its source is
  exhausted (or with no source at all), so you can write to it interactively via
  `RunningProcess.TakeStdin` — see [Streaming & interactive I/O](streaming.md).

### Unix privilege drop & session detach

Four **Unix-only** builders drop the child's privileges or detach its session, for
running a helper as a less-privileged user (daemons, CI runners, sandboxes):

- **`Uid(uid)`** / **`Gid(gid)`** run the child under a different user / group id
  (`setuid` / `setgid`). **`User(uid, gid)`** is the common pair, equal to
  `.Gid(gid).Uid(uid)`.
- **`Setsid()`** detaches the child into a **new session** (`setsid()`): its own
  session and process group, no controlling terminal.

```fsharp
task {
    // Drop to uid/gid 1000 and detach into a new session (needs privilege to run as another user).
    let! _ = (Command.create "worker" |> Command.user 1000 1000 |> Command.setsid).RunAsync()
    ()
}
```

```csharp
// Drop to uid/gid 1000 and detach into a new session.
await new Command("worker").User(1000, 1000).Setsid().RunAsync();
```

Honest by construction — never a silent downgrade:

- On **Windows** (no equivalent) any of these fails the spawn with
  `ProcessError.Unsupported`, exactly like `Umask`.
- A **uid/gid drop the caller can't make** fails with `ProcessError.Spawn`, never a
  child that kept the parent's ids. The up-front check is deliberately **root-only**:
  dropping to another user is allowed only when the caller is root (`euid == 0`). A
  non-root caller is refused before the spawn — *including* one that holds
  `CAP_SETUID` / `CAP_SETGID` (a rootless container / sandbox), which is conservatively
  declined rather than probed (`setpriv` remains the real arbiter, so the guard stays a
  simple root gate rather than a partial reimplementation of the kernel's capability
  model). The drop also *clears* the parent's supplementary groups and applies `setgid`
  **before** `setuid`, so it composes into a correct drop.
- **Containment is preserved under `Setsid`.** A new session still makes the child
  its own process-group leader, so the kill-on-drop group teardown reaches it. (The
  session detach replaces the group's default `POSIX_SPAWN_SETPGROUP` for that one
  command; it is never combined with it.)

Because `posix_spawn` has no uid/gid attribute (and forking a managed .NET runtime
to drop privileges in the child is unsafe), a command requesting `Uid`/`Gid` is
rewritten to run through the **`setpriv`** helper (util-linux): it sets the gid/uid
and clears the supplementary groups, then `exec`s the real program *in place* (same
pid, so containment is unchanged). `setpriv` ships on mainstream Linux; where it is
absent (macOS/BSD) a `Uid`/`Gid` drop fails with a typed `ProcessError.Spawn` naming
the missing helper. `Setsid` alone needs no helper (it is a native `posix_spawn`
attribute).

ProcessKit wires **pipes**, not a pseudo-terminal, so a tool that *demands* a tty
— an `ssh` / `sudo` **password** prompt, some credential helpers — won't get one.
Drive such tools non-interactively instead (key-based auth, `ssh -o BatchMode=yes`,
`GIT_TERMINAL_PROMPT=0`), or feed a known answer over
[interactive stdin](streaming.md).

## Consuming verbs

The builder describes the run; the verb you finish with decides what you get back.
Every verb returns `Task<Result<_, ProcessError>>`, and every verb takes an optional
`CancellationToken` (omit it, or pass one: `cmd.RunAsync()` / `cmd.RunAsync(ct)`).

| Verb | `Ok` payload | Non-zero exit | Use when |
|---|---|---|---|
| `OutputStringAsync()` | `ProcessResult<string>` | captured (data) | You want to inspect the outcome yourself |
| `OutputBytesAsync()` | `ProcessResult<byte[]>` | captured (data) | Binary stdout (images, archives, …) |
| `RunAsync()` | trimmed `string` | `ProcessError.Exit` | "Give me the answer or fail" |
| `RunUnitAsync()` | `unit` | `ProcessError.Exit` | You only care that it succeeded |
| `ExitCodeAsync()` | `int` | the code, as `Ok` | The code *is* the answer |
| `ProbeAsync()` | `bool` | `0`→`true`, `1`→`false`, else error | Predicate commands: `git diff --quiet`, `grep -q` |
| `ParseAsync(f)` / `TryParseAsync(f)` | `'T` | `ProcessError.Exit` | A typed value from stdout (success required) |
| `FirstLineAsync(p)` | `string option` | — (stream-based) | Grab one matching line, kill the rest |
| `StartAsync()` | `RunningProcess` | — | A live handle: [streaming, stdin, probes](streaming.md) |

`RunAsync` returns stdout with trailing whitespace trimmed. `ExitCodeAsync` hands back a
non-zero exit as `Ok` data, but a signal kill or timeout errors rather than
inventing a sentinel like `-1`. `ProbeAsync` errors on any exit other than `0` or `1`.
`ParseAsync` maps the trimmed stdout through `f` (a thrown parser becomes
`ProcessError.Parse`); `TryParseAsync` takes the standard .NET try-parse shape —
pass a `bool TryX(string, out 'T)` such as `int.TryParse`, with an explicit type
argument (`TryParseAsync<int>(int.TryParse)`, since the BCL parsers are overloaded) —
and turns a `false` return into `ProcessError.Parse`. (From F#, `Runner.tryParse` keeps the
`Result<'T, string>`-returning shape, so the parser can supply its own error message.)
`FirstLineAsync` returns the first
stdout line matching the predicate and kills the (private-group) child the moment
it has its answer — you never wait out a long log for one line — and returns
`Ok None` when stdout closes without a match.

**F#**

```fsharp
task {
    // Probe: the exit code as a yes/no.
    match! (Command.create "git" |> Command.args [ "diff"; "--quiet" ]).ProbeAsync() with
    | Ok true -> printfn "working tree clean"
    | Ok false -> printfn "there are changes"
    | Error err -> eprintfn $"{err.Message}"

    // Parse: a typed value from stdout.
    let! version = (Command.create "node" |> Command.arg "--version").ParseAsync(fun s -> s.TrimStart('v'))

    // FirstLine: stop as soon as the interesting line appears.
    match! (Command.create "git" |> Command.args [ "log"; "--oneline" ]).FirstLineAsync(fun l -> l.Contains "fix:") with
    | Ok(Some line) -> printfn $"{line}"
    | Ok None -> printfn "no fix commit"
    | Error err -> eprintfn $"{err.Message}"
}
```

**C#**

```csharp
// Probe: the exit code as a yes/no.
Console.WriteLine(await new Command("git").Args(["diff", "--quiet"]).ProbeAsync() switch
{
    { IsOk: true, ResultValue: true }     => "working tree clean",
    { IsOk: true, ResultValue: false }    => "there are changes",
    { IsOk: false, ErrorValue: var err } => err.Message,
});

// Parse: a typed value from stdout.
var version = await new Command("node").Arg("--version").ParseAsync(s => s.TrimStart('v'));

// FirstLine: stop as soon as the interesting line appears.
Console.WriteLine(await new Command("git").Args(["log", "--oneline"]).FirstLineAsync(l => l.Contains("fix:")) switch
{
    { IsOk: true, ResultValue: { Value: var line } } => line,            // Some(line)
    { IsOk: true }                   => "no fix commit", // None
    { IsOk: false, ErrorValue: var err }            => err.Message,
});
```

The same vocabulary repeats on every layer. To run a verb through a specific
`IProcessRunner` — the dependency-injection and test seam — go through the
`Runner` module (`Runner.run runner CancellationToken.None cmd`); the verbs also
exist on [`CliClient`](testing.md), [`Pipeline`](pipelines.md), and as the
`Exec.*` one-liners.

## Results

The capturing verbs (`OutputStringAsync` / `OutputBytesAsync`) hand back a
`ProcessResult<'T>` — a non-zero exit is **data** here, not an error:

**F#**

```fsharp
task {
    match! (Command.create "git" |> Command.args [ "merge"; "feature" ]).OutputStringAsync() with
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

**C#**

```csharp
if ((await new Command("git").Args(["merge", "feature"]).OutputStringAsync()).TryGetValue(out var result, out var runErr))
{
    Console.WriteLine($"code={result.Code} success={result.IsSuccess} timedOut={result.IsTimedOut}");
    Console.WriteLine($"took {result.Duration}, truncated={result.Truncated}");

    // Opt into erroring whenever you're ready:
    Console.WriteLine((result.EnsureSuccess()) switch
    {
        { IsOk: true, ResultValue: var ok }   => ok.Stdout,
        { IsOk: false, ErrorValue: var err } => err.Message,
    });
}
else
    Console.Error.WriteLine(runErr.Message);
```

The accessors:

| Member | Meaning |
|---|---|
| `Stdout` | Captured stdout — `string` (text verbs) or `byte[]` (bytes verbs); carries the merged stdout+stderr under `MergeStderr` |
| `Stderr` | Captured stderr, as decoded text (empty under `MergeStderr` — the stderr is merged into `Stdout`) |
| `Code` | The exit code, or `None` for a signal kill / timeout |
| `Signal` | The terminating signal number (Unix), else `None` |
| `IsSuccess` | The code is in `AcceptedCodes` (`{0}` by default) |
| `IsTimedOut` | The run's own deadline expired |
| `Outcome` | The three-way enum behind the accessors above |
| `Duration` | Wall-clock duration of the run |
| `Truncated` | A buffer policy dropped output |
| `AcceptedCodes` | The exit codes treated as success — `OkCodes` (`{0}` by default) |
| `Combined` | Stdout and stderr joined (stdout, then stderr on a new line when both are non-empty) — a post-hoc concatenation, *not* the real interleaving; use `MergeStderr` for a byte-exact `2>&1` |
| `OutputContainsAny(needles)` | Case-insensitive search of both streams — for the "a known marker makes a non-zero exit benign" idiom below |

`ProcessResult.ensureSuccess` (or the instance `result.EnsureSuccess()`) converts a
`ProcessResult<'T>` — text or bytes — into a `Result`: the result unchanged on success,
otherwise the matching `ProcessError` (`Exit` / `Signalled` / `Timeout`).

### Accepting non-zero exits

Some tools use a non-zero exit as information (`grep` returns `1` for "no match").
Tell ProcessKit which codes count as success with `OkCodes`:

**F#**

```fsharp
task {
    let grep =
        Command.create "grep"
        |> Command.args [ "ERROR"; "app.log" ]
        |> Command.okCodes [ 0; 1 ] // 1 ("no match") is success, not failure

    match! grep.RunAsync() with
    | Ok output -> printfn $"matches:\n{output}"
    | Error err -> eprintfn $"{err.Message}" // a real failure (e.g. exit 2)
}
```

**C#**

```csharp
var grep =
    new Command("grep")
        .Args(["ERROR", "app.log"])
        .OkCodes([0, 1]); // 1 ("no match") is success, not failure

Console.WriteLine(await grep.RunAsync() switch
{
    { IsOk: true, ResultValue: var output } => $"matches:\n{output}",
    { IsOk: false, ErrorValue: var err }   => err.Message, // a real failure (e.g. exit 2)
});
```

`OkCodes` sets which exit codes `ProcessResult.IsSuccess`, `ensureSuccess`, and
`RunAsync` / `RunUnitAsync` accept. The codes *replace* the default rather than adding to it, so
include `0` if you still want it (as `[ 0; 1 ]` above does); an empty set is a **no-op** that
keeps the previously configured codes (it never clears them).

### The `Outcome` enum

When the distinction matters, match on `Outcome` instead of decoding the
`Code` / `IsTimedOut` pair. There are four cases — the fourth, `Unobserved`, is
the rare honest fallback for a process that concluded but whose actual exit
status could not be observed (a native API failure, or an unresolved POSIX
reap race); it is never a stand-in for a clean exit, and (like `Signalled` /
`TimedOut`) never counts as success:

**F#**

```fsharp
match result.Outcome with
| Outcome.Exited 0 -> printfn "clean"
| Outcome.Exited code -> printfn $"failed with {code}"
| Outcome.Signalled signal -> printfn $"killed by signal {signal}"
| Outcome.TimedOut -> printfn "hit its deadline"
| Outcome.Unobserved reason -> printfn $"exit status unknown: {reason}"
```

**C#**

```csharp
Console.WriteLine(result.Outcome switch
{
    { IsExited: true, Code.Value: 0 }               => "clean",
    { IsExited: true, Code.Value: var code }        => $"failed with {code}",
    { IsSignalled: true, Signal.Value: var signal } => $"killed by signal {signal}",
    { IsSignalled: true }                           => "killed by an unknown signal",
    { IsUnobserved: true }                          => "exit status unknown",
    _                                                => "hit its deadline", // TimedOut
});
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
(`RunAsync` / `RunUnitAsync` / `ParseAsync` / `TryParseAsync`) additionally turn a non-zero exit into
`ProcessError.Exit`.

**F#**

```fsharp
task {
    match! (Command.create "deploy").RunAsync() with
    | Ok out -> printfn $"{out}"
    | Error(ProcessError.NotFound(program, _)) -> eprintfn $"not installed: {program}"
    | Error(ProcessError.Exit(program, code, _, stderr)) -> eprintfn $"{program} exited {code}: {stderr}"
    | Error(ProcessError.Timeout(program, t, _, _)) -> eprintfn $"{program} timed out after {t}"
    | Error err -> eprintfn $"{err.Message}"
}
```

**C#**

```csharp
Console.WriteLine(await new Command("deploy").RunAsync() switch
{
    { IsOk: true, ResultValue: var output }               => output,
    { IsOk: false, ErrorValue: ProcessError.NotFound n } => $"not installed: {n.Program}",
    { IsOk: false, ErrorValue: ProcessError.Exit e }     => $"{e.Program} exited {e.Code}: {e.Stderr}",
    { IsOk: false, ErrorValue: ProcessError.Timeout t }  => $"{t.Program} timed out after {t.Timeout}",
    { IsOk: false, ErrorValue: var err }                 => err.Message,
});
```

| Variant | Fields | Meaning |
|---|---|---|
| `ProcessError.Spawn` | `program, detail` | The program was located but the OS couldn't start it (permissions, a bad working directory, a Windows `.cmd`/`.bat` needing `cmd.exe`, …). **Not** `isNotFound`. |
| `ProcessError.NotFound` | `program, Searched: string option` | The program couldn't be located (`isNotFound` is `true`); `searched` is the probed path when known. |
| `ProcessError.Exit` | `program, code, stdout, stderr` | A success-requiring verb saw a non-zero exit; both streams attached in full. |
| `ProcessError.Signalled` | `program, signal: int option, stdout, stderr` | Killed by a signal with no exit code; `signal` carries the number on Unix, `None` elsewhere; the partial streams captured before the kill are attached. |
| `ProcessError.Timeout` | `program, timeout, stdout, stderr` | The run's own deadline killed it; whatever it captured before the kill is attached. |
| `ProcessError.NotReady` | `program, timeout` | A [readiness probe](streaming.md) gave up — distinct from a timeout. |
| `ProcessError.Parse` | `program, detail` | A `ParseAsync` / `TryParseAsync` parser rejected the output. |
| `ProcessError.OutputTooLarge` | `program, lineLimit, byteLimit, totalLines, totalBytes` | A `FailLoud` (`OverflowMode.Error`) buffer ceiling was exceeded. |
| `ProcessError.Stdin` | `program, detail` | The child's stdin source could not be read — a missing/unreadable `FromFile` path, say — on an otherwise-successful run. A routine broken pipe (the child closed stdin early, as `head` does) is never reported, and a louder exit/signal/timeout failure wins instead. Also surfaces for a pipeline's first stage. |
| `ProcessError.CassetteMiss` | `program` | A record/replay cassette found no matching recording — kept distinct from not-found, so `isNotFound` is `false`. |
| `ProcessError.Unsupported` | `operation` | The platform can't do what was asked (e.g. a POSIX signal on Windows) and silently skipping would be wrong. |
| `ProcessError.Cancelled` | `program` | The run's `CancellationToken` fired. Always an error. |
| `ProcessError.ResourceLimit` | `detail` | A requested [resource cap](process-groups.md) couldn't be enforced. |
| `ProcessError.Io` | `detail` | A low-level I/O failure from ProcessKit's own machinery (driving a child, group control, cassette files). |

Two classifiers help with retry and diagnostic logic:

**F#**

```fsharp
match! cmd.RunAsync() with
| Ok _ -> ()
| Error err when ProcessError.isNotFound err -> installThenRetry () // NotFound only
| Error err when ProcessError.isTransient err -> scheduleRetry () // Spawn / Io blips
| Error err -> fail err
```

**C#**

```csharp
switch (await cmd.RunAsync())
{
    case { IsOk: true }:
        break;
    case { IsOk: false, ErrorValue: { IsNotFound: true } }: // NotFound only
        installThenRetry();
        break;
    case { IsOk: false, ErrorValue: { IsTransient: true } }: // Spawn / Io blips
        scheduleRetry();
        break;
    case { IsOk: false, ErrorValue: var err }:
        fail(err);
        break;
}
```

`ProcessError.isNotFound` is `true` only for `NotFound`; `ProcessError.isTransient`
is `true` for `Spawn` and `Io` — failures that may succeed on a retry. From C# these are
the instance forms `err.IsNotFound` and `err.IsTransient`.

To read a failure's fields without matching every case — the only practical way from C#, which
can't destructure an F# union — `ProcessError` exposes `.Program`, `.Stdout`, `.Stderr`,
`.Combined`, `.Code`, and `.Signal`, each an `option`/`Option<T>` populated for the cases that
carry that field (e.g. `.Code` is set only on `Exit`, `.Stdout`/`.Stderr`/`.Combined` on
`Exit`/`Signalled`/`Timeout`) and `None` elsewhere. The generated `err.IsExit` / `IsSignalled` /
`IsTimeout` / `IsCancelled` case testers pair with them.

---

Next: [Streaming & interactive I/O](streaming.md) ·
[Timeouts, retries & cancellation](timeouts-and-cancellation.md) ·
[Process groups](process-groups.md)
