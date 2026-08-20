# Pseudo-terminal (PTY)

[Previous: Overview](./)

A pseudo-terminal (PTY) gives a child process a real terminal instead of the usual stdin/stdout/stderr pipes. Use it for programs that change behaviour when `isatty` is true: password prompts, SSH-style authentication, terminal UIs, and tools that refuse to prompt without a terminal.

`Command.Pty()` enables a PTY with the default `PtyConfig` (80 columns, 24 rows, echo on). `Command.Pty(config)` lets you choose the initial terminal geometry and whether typed input is echoed. A PTY has **one merged terminal stream**: stdout and stderr are interleaved in `Stdout`, `OutputEvent.Stderr` is never produced, and `ProcessResult.Stderr` is empty.

## PTY or pipes?

Prefer ordinary pipes for non-interactive commands: they preserve separate stdout and stderr, are available on every supported host, and are usually the simplest choice. Choose a PTY only when the child actually needs terminal semantics or when a single terminal-style output stream is what you want.

PTY mode cannot be combined with the separate-stderr observation hooks (`StderrTee` and `OnStderrLine`), `Setsid`, or a non-final pipeline stage. These combinations are rejected at the builder boundary rather than silently changing the child’s I/O.

## Basic PTY run

**F#**

```fsharp
open ProcessKit

task {
    let command = Command.create "my-terminal-tool" |> Command.pty

    match! command.OutputStringAsync() with
    | Ok result ->
        // result.Stdout contains the one merged terminal stream.
        printfn $"{result.Stdout}"
    | Error error -> eprintfn $"{error.Message}"
}
```

**C#**

```csharp
using System;
using ProcessKit;

var command = new Command("my-terminal-tool").Pty();
var result = await command.OutputStringAsync();

Console.WriteLine(result switch
{
    { IsOk: true, ResultValue: var run } => run.Stdout, // merged terminal stream
    { IsOk: false, ErrorValue: var error } => error.Message,
});
```

## Password-style prompt without echoing the secret

Keep stdin open, write the credential only after the child starts, and close stdin when input is complete. `Echo = false` disables the POSIX PTY slave’s cooked-mode `ECHO` bit, so input written through the PTY is not copied into captured output.

**F#**

```fsharp
open ProcessKit

task {
    let command =
        (Command.create "/bin/sh"
         |> Command.args [ "-c"; "printf 'Password: '; IFS= read -r password; printf 'OK\\n'" ])
            .Pty({ PtyConfig.Default with Echo = false })
            .KeepStdinOpen()

    match! command.StartAsync() with
    | Error error -> eprintfn $"{error.Message}"
    | Ok process ->
        use process = process

        match process.TakeStdin() with
        | Some stdin ->
            do! stdin.WriteLineAsync "credential-from-a-secret-store"
            do! stdin.FinishAsync()
        | None -> failwith "PTY stdin was not available"

        let enumerator = process.StdoutLinesAsync().GetAsyncEnumerator()

        try
            let mutable more = true

            while more do
                let! moved = enumerator.MoveNextAsync().AsTask()

                if moved then
                    printfn $"> {enumerator.Current}"
                else
                    more <- false
        finally
            enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult()
}
```

**C#**

```csharp
using System;
using ProcessKit;

var command = new Command("/bin/sh")
    .Args(["-c", "printf 'Password: '; IFS= read -r password; printf 'OK\\n'"])
    .Pty(new PtyConfig(80, 24, false))
    .KeepStdinOpen();

await using var process = (await command.StartAsync()).GetValueOrThrow();

if (process.TakeStdin() is { Value: var stdin })
{
    await stdin.WriteLineAsync("credential-from-a-secret-store");
    await stdin.FinishAsync(); // EOF lets the prompt finish.
}

await foreach (var line in process.StdoutLinesAsync())
    Console.WriteLine($"> {line}");
```

On Windows, echo is controlled by the child program’s console mode; ConPTY cannot force it off before the child starts. A Windows password prompt must therefore suppress its own echo. Never log a secret or place it in a recording. The [testing guide](testing.md#pseudo-terminal-pty-doubles) describes the PTY double and cassette redaction boundary.

`ProcessStdin.WriteLineAsync` sends LF to a plain pipe or POSIX PTY and CR to Windows ConPTY, where
the virtual-terminal input path interprets it as Enter. `WriteAsync` remains byte-exact when a child
needs an explicit sequence. `PtySession.SendLineAsync` separately follows
`PtySessionOptions.LineEnding`; its `Auto` default uses the carriage return a terminal sends for any
PTY and LF for a plain pipe.

The [cookbook PTY recipe](cookbook.md#interactive-password-prompt-through-a-pty) contains the same pattern in context.

### Ending stdin on a PTY

A PTY has one device for input and output, so ending stdin is not the handle close it is for a pipe.
On POSIX, `ProcessStdin.FinishAsync` (and `PtySession.CloseStdinAsync`, and a `Command.Stdin` source
once the run is done delivering it — drained, or failed to open) instead sends the terminal's own
end-of-input character — the pty's configured `termios.c_cc[VEOF]`, Ctrl-D on a default terminal —
twice: the first ends a line the input left unterminated, the second lands on the now-empty line and
is what makes the child's next read return zero bytes. A child that reads to EOF, such as `cat` or a
shell `read` loop, therefore finishes even when the last input carried no newline. The terminal
itself stays open for the child's output until the run ends.

Because this is a character the line discipline interprets, it only ends the input of a child whose
terminal is in canonical (cooked) mode. A child that switches its own tty to raw mode receives that
byte as ordinary input, as it would from a real terminal; end it by stopping the child instead.

On Windows the same three callers send the console's own end-of-input gesture instead: Ctrl-Z followed
by Enter, the end of input `copy con` has always been finished with. The pseudoconsole's input stays
open either way, and for the same reason: closing it asks the console host to end the whole session
rather than telling the child its input is over, which can tear down a child that has not even reached
its first read. A Windows PTY run therefore holds that input open for the child's whole lifetime —
including a run with no stdin source and no `KeepStdinOpen` at all — and closes it once the child has
exited. The cooked-mode caveat applies here too: a child whose `CONIN$` console mode is no longer in line
mode reads Ctrl-Z as ordinary input. And as on POSIX, a child that reads to end of input needs a source
or an explicit finish to see one.

A delivery that genuinely fails is reported — an `IOException` from `FinishAsync`, a typed
`ProcessError.Io` from `CloseStdinAsync` — rather than leaving the child waiting.

## Automating an interactive CLI (expect and send)

Driving a real interactive program — `ssh`, a database or language REPL, an installer that asks
questions — is the reason a PTY exists. `PtySession` is that loop: wait for a pattern in the child's
terminal output, send it an answer, repeat.

A prompt is **not a line**. `Password: `, `> ` and `(y/N) ` carry no line terminator, because the
program prints them and then blocks waiting for the very input the prompt asks for. That newline never
arrives, so `WaitForLineAsync` — which frames the stream into lines — can never deliver such a prompt.
A session therefore reads the merged terminal stream as raw text and matches patterns against a
sliding window of it, framing nothing.

Each wait has its **own** deadline, separate from the run-wide `Command.Timeout`: a pattern that does
not arrive returns `ProcessError.NotReady` and leaves the child running, so the script can try
something else. A wait also ends promptly if the child's output ends first, rather than burning the
rest of its budget on output that can no longer come.

### The expect/send loop

The child below prints two prompts — the second only after the first is answered — then prints a
result. `Echo = false` keeps the answers out of the terminal's own output.

**F#**

```fsharp
open System
open ProcessKit

task {
    let script =
        "printf 'name> '; IFS= read -r name; printf 'city> '; IFS= read -r city; "
        + "printf 'HELLO %s OF %s\\n' \"$name\" \"$city\""

    let command =
        (Command.create "/bin/sh" |> Command.args [ "-c"; script ])
            .Pty({ PtyConfig.Default with Echo = false })
        |> Command.keepStdinOpen
        |> Command.timeout (TimeSpan.FromMinutes 2.0)

    match! command.StartAsync() with
    | Error error -> eprintfn $"{error.Message}"
    | Ok started ->
        use started = started
        let session = PtySession started

        let answer (prompt: string) (reply: string) =
            task {
                match! session.ExpectAsync(prompt, TimeSpan.FromSeconds 30.0) with
                | Error error -> return Error error
                | Ok _ -> return! session.SendLineAsync reply
            }

        match! answer "name> " "ada" with
        | Error error -> eprintfn $"{error.Message}"
        | Ok() ->
            match! answer "city> " "london" with
            | Error error -> eprintfn $"{error.Message}"
            | Ok() ->
                match! session.ExpectAsync("HELLO ", TimeSpan.FromSeconds 30.0) with
                | Ok matched -> printfn $"greeted: {matched.Text}{session.Pending}"
                | Error error -> eprintfn $"{error.Message}"

                let! outcome = session.WaitForExitAsync()
                printfn $"{outcome}"
                // The transcript is the whole conversation, for a failure report.
                eprintfn $"{session.Transcript}"
}
```

**C#**

```csharp
using System;
using ProcessKit;

const string Script =
    "printf 'name> '; IFS= read -r name; printf 'city> '; IFS= read -r city; " +
    "printf 'HELLO %s OF %s\\n' \"$name\" \"$city\"";

var command = new Command("/bin/sh")
    .Args(["-c", Script])
    .Pty(new PtyConfig(80, 24, false)) // Echo off: answers stay out of the terminal output.
    .KeepStdinOpen()
    .Timeout(TimeSpan.FromMinutes(2));

await using var started = (await command.StartAsync()).GetValueOrThrow();
var session = new PtySession(started);

foreach (var (prompt, reply) in new[] { ("name> ", "ada"), ("city> ", "london") })
{
    // Each wait gets its own budget; the run-wide timeout is untouched by it.
    (await session.ExpectAsync(prompt, TimeSpan.FromSeconds(30))).GetValueOrThrow();
    (await session.SendLineAsync(reply)).GetValueOrThrow();
}

var greeting = (await session.ExpectAsync("HELLO ", TimeSpan.FromSeconds(30))).GetValueOrThrow();
Console.WriteLine($"greeted: {greeting.Text}{session.Pending}");

Console.WriteLine(await session.WaitForExitAsync());
```

`ExpectAsync` also takes a `Regex` for a prompt that varies (`new Regex(@"psql \(\d+\.\d+\)")`), with
the same contract. Every string or regex match must consume at least one character; an empty string,
or an empty, anchor-only, or lookaround regex match, returns the typed `ProcessError.Unsupported`
result so an expect loop cannot repeatedly consume an unchanged window. Regex matching runs over the
unframed session view rather than one line, so `^`/`$` anchor to the window unless you pass
`RegexOptions.Multiline`.

Each pattern deadline uses the command's `TimeProvider` (`TimeProvider.System` by default), just like
`WaitForLineAsync`. Attach a deterministic provider with `Command.TimeProvider(provider)` /
`Command.timeProvider provider` when a test should advance an expect timeout without sleeping.

### What the session owns

- **The output pipes.** Creating a session claims the handle exactly like `OutputEventsAsync` does, so
  a capturing or streaming verb afterwards is refused, and a second session over the same handle
  throws. Ask `WaitForExitAsync` for the outcome, and dispose the `RunningProcess` (or its owning
  `ProcessGroup`) to reap the tree.
- **Nothing else.** `SendAsync`/`SendLineAsync` write through the same interactive stdin
  `TakeStdin` hands out — there is no second channel — so the run needs `Command.KeepStdinOpen`. Without
  it the send verbs return a typed `ProcessError.Unsupported` rather than dropping the bytes.
- **A bounded memory footprint.** `PtySessionOptions.WindowChars` (65536 by default) caps the
  not-yet-matched window and `TranscriptChars` (1048576) caps the transcript; both drop the oldest text
  and report it through `WindowTruncated`/`TranscriptTruncated`. A pattern needing more context than
  the window holds cannot match — raise the window rather than expecting it to.

`SendLineAsync` ends its line the way a terminal does: a carriage return, which a POSIX pty's line
discipline turns into a newline for the child and ConPTY turns into an Enter key event. Override it
with `PtySessionOptions.LineEnding` for a child that wants something else. Because terminals end
*their* lines with `\r\n`, match on the prompt text itself rather than on a trailing `\n`.

Interaction works against a plain (non-PTY) run too, but the child decides whether a prompt is ever
visible: without a terminal most programs switch stdout to block buffering and the prompt stays in the
child's own buffer — which is exactly what `Command.Pty` is for.

### ANSI/VT-decorated prompts

Terminal programs often decorate prompts with colour (CSI sequences) or emit OSC title/hyperlink
controls. The ordinary `PtySession` constructors preserve that raw terminal text. Opt into a cleaned
session when patterns and diagnostics should see only visible text:

```fsharp
let session = PtySession.WithAnsiFiltering proc
let! prompt = session.ExpectAsync("Password: ", TimeSpan.FromSeconds 30.0)
```

```csharp
var session = PtySession.WithAnsiFiltering(proc);
var prompt = await session.ExpectAsync("Password: ", TimeSpan.FromSeconds(30));
```

Filtering applies consistently to matching, `Pending`, and `Transcript`. It is incremental, so CSI,
OSC (BEL or ST terminated), and single-ESC controls are removed even when a read boundary lands inside
the sequence. The byte-exact `Command.StdoutTee`/`StderrTee` sinks remain raw. The same factory works
with `FakeProcess.WithPty()`, which lets tests exercise the cleaned conversation without a real PTY.

### Secrets in a transcript

`Transcript` records what the **child** printed; input sent through the session is never added to it,
logged, or traced. A terminal with echo on, however, reflects typed input into its own output — so a
password sent to a `PtyConfig.Echo = true` run does reach the transcript by that route. For a
credential exchange, use `Echo = false` (POSIX), set `CaptureTranscript = false`, or both.

The [testing guide](testing.md#pseudo-terminal-pty-doubles) covers driving a session against the PTY
double, with no real process involved.

## Resizing a live terminal

`RunningProcess.ResizeAsync(cols, rows)` changes the geometry of a live PTY. It resizes ConPTY on Windows and applies `TIOCSWINSZ` followed by `SIGWINCH` on POSIX, so terminal UIs can reflow. It can be called before or after a stream has been claimed. Dimensions must be between 1 and `Int16.MaxValue`; invalid values throw `ArgumentOutOfRangeException`.

Calling it on a non-PTY `RunningProcess` returns `Error (ProcessError.Unsupported ...)` (or the equivalent C# `Result` error), never a successful no-op. Test doubles deliberately differ here: a PTY fake records resize as a no-op success; see [testing](testing.md#pseudo-terminal-pty-doubles).

## Platform support

PTY support is available on Windows through ConPTY (Windows 10 1809+) and on Linux through `openpty` plus `setsid --ctty` — the latter loaded from a trusted system directory (`/usr/bin`, `/bin`, `/usr/sbin`, `/sbin`) rather than `PATH`, so it cannot be replaced by a planted binary ([why](hardening.md#where-the-unix-helper-binaries-come-from)); unsupported hosts return `ProcessError.Unsupported` rather than falling back to pipes. See the full [platform capability matrix](platform-support.md#pseudo-terminal-pty-capabilities), including macOS/BSD helper requirements and containment caveats.

Every Windows ConPTY child starts with `CREATE_NEW_PROCESS_GROUP`, regardless of
`WindowsCtrlSignals()`, so a CTRL+C broadcast on the caller's shared console cannot terminate the
isolated terminal child. Windows also disables default CTRL+C handling for a process created with
that flag. Consequently, sending U+0003 (Ctrl+C) through `SendAsync` or the interactive stdin does
not interrupt a ConPTY child by default, unlike a POSIX pty where the terminal's `VINTR` normally
delivers `SIGINT`. `WindowsCtrlSignals()` does not add the process-group flag; it only opts the
leader into ProcessKit's best-effort targeted CTRL+BREAK path for `Signal(Int/Term)`.

A ConPTY child's standard handles always come from the pseudoconsole, never from the launcher, so
its output reaches the run's merged stream in both Windows launch environments — a console-attached
one (a terminal, a debugger, a console-hosted test runner) and a headless one (a service-hosted CI
step, a redirected test host). The two need different mechanisms: a console-attached launcher severs
its console handles in the child's startup information, while a headless launcher instead replaces
its own three standard-handle slots with null for the length of the `CreateProcess` call and restores
them immediately afterwards. That short launcher-side window is serialized with every ProcessKit
Windows spawn, so no command started through ProcessKit — including one inheriting the caller's stdio
— can observe it. It cannot coordinate anything else: code outside ProcessKit that spawns with
inherited stdio on another thread, or that reads `Console` for the first time during the window, can
still race it. If you need strict isolation from such activity, run PTY sessions from a dedicated
helper process.

---

Next: [Pipelines](pipelines.md)
