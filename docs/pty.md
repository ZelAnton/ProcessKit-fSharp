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

The [cookbook PTY recipe](cookbook.md#interactive-password-prompt-through-a-pty) contains the same pattern in context.

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
the same contract. Matching runs over raw terminal text rather than one line, so `^`/`$` anchor to the
window unless you pass `RegexOptions.Multiline`.

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

PTY support is available on Windows through ConPTY (Windows 10 1809+) and on Linux through `openpty` plus `setsid --ctty`; unsupported hosts return `ProcessError.Unsupported` rather than falling back to pipes. See the full [platform capability matrix](platform-support.md#pseudo-terminal-pty-capabilities), including macOS/BSD helper requirements and containment caveats.

---

Next: [Pipelines](pipelines.md)
