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

## Resizing a live terminal

`RunningProcess.ResizeAsync(cols, rows)` changes the geometry of a live PTY. It resizes ConPTY on Windows and applies `TIOCSWINSZ` followed by `SIGWINCH` on POSIX, so terminal UIs can reflow. It can be called before or after a stream has been claimed. Dimensions must be between 1 and `Int16.MaxValue`; invalid values throw `ArgumentOutOfRangeException`.

Calling it on a non-PTY `RunningProcess` returns `Error (ProcessError.Unsupported ...)` (or the equivalent C# `Result` error), never a successful no-op. Test doubles deliberately differ here: a PTY fake records resize as a no-op success; see [testing](testing.md#pseudo-terminal-pty-doubles).

## Platform support

PTY support is available on Windows through ConPTY (Windows 10 1809+) and on Linux through `openpty` plus `setsid --ctty`; unsupported hosts return `ProcessError.Unsupported` rather than falling back to pipes. See the full [platform capability matrix](platform-support.md#pseudo-terminal-pty-capabilities), including macOS/BSD helper requirements and containment caveats.

---

Next: [Pipelines](pipelines.md)
