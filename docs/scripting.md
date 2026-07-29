# Scripting with F# Interactive

[Previous: Cookbook](cookbook.md)

ProcessKit works well as the process layer in an `.fsx` script: you keep the quick edit-run loop of
shell scripting, but arguments, outcomes, cancellation, and pipelines remain typed. Commands are
launched directly rather than through a shell, and every ordinary run stays inside ProcessKit's
kill-on-dispose containment.

- [Load the package](#load-the-package)
- [Cross the asynchronous boundary once](#cross-the-asynchronous-boundary-once)
- [Use `Exec` for one-off tools](#use-exec-for-one-off-tools)
- [Use `CliClient` for a tool you call repeatedly](#use-cliclient-for-a-tool-you-call-repeatedly)
- [Build pipelines without shell syntax](#build-pipelines-without-shell-syntax)
- [Handle Ctrl+C without orphaning the run](#handle-ctrlc-without-orphaning-the-run)
- [Return an honest exit code](#return-an-honest-exit-code)
- [Resolve tools portably](#resolve-tools-portably)
- [Cross-platform checklist](#cross-platform-checklist)

## Load the package

Put the NuGet reference at the top of the script:

```text
#r "nuget: ProcessKit"
```

For repeatable automation, pin the exact package version you have tested:
`#r "nuget: ProcessKit, <version>"`. F# Interactive restores the package on the first run and reuses
the NuGet cache afterwards. Then import the namespaces your script needs:

```fsharp
open System
open System.Threading
open ProcessKit
```

Run the file with `dotnet fsi build.fsx`. Use `--` before arguments intended for your script:
`dotnet fsi build.fsx -- --configuration Release`.

The `#r` line is shown as text because it is an FSI directive, not valid in a compiled `.fs` file.
Every executable `fsharp` block on this page is still extracted and compiled against the current
ProcessKit assemblies by `scripts/verify-doc-snippets.ps1`.

## Cross the asynchronous boundary once

ProcessKit verbs return `Task<Result<_, ProcessError>>`. Keep composition asynchronous in functions,
then block once at the script's top level; repeated `.Result` calls inside the workflow make error
handling harder and can serialize work that should overlap.

```fsharp
let inspectRepository () =
    task {
        match! Exec.run "git" [ "rev-parse"; "--show-toplevel" ] with
        | Ok root ->
            printfn $"repository: {root}"
            return Ok()
        | Error error -> return Error error
    }

let inspection =
    inspectRepository().GetAwaiter().GetResult()

match inspection with
| Ok() -> ()
| Error error -> eprintfn $"inspection failed: {error.Message}"
```

`GetAwaiter().GetResult()` preserves the original exception if the script itself has a bug. Normal
process failures are not exceptions here: they remain the `Error ProcessError` value you match.

## Use `Exec` for one-off tools

`Exec` is the shortest route for a command used once. `Exec.run` requires an accepted exit and returns
trimmed stdout; `Exec.outputString` returns the full `ProcessResult<string>`, where a non-zero exit is
data rather than an `Error`.

```fsharp
let version =
    Exec.run "dotnet" [ "--version" ]
    |> fun pending -> pending.GetAwaiter().GetResult()

match version with
| Ok value -> printfn $"SDK {value}"
| Error error -> eprintfn $"dotnet failed: {error.Message}"

let status =
    Exec.outputString "git" [ "status"; "--short" ]
    |> fun pending -> pending.GetAwaiter().GetResult()

match status with
| Ok result ->
    printf "%s" result.Stdout
    eprintf "%s" result.Stderr
| Error error -> eprintfn $"git could not be run: {error.Message}"
```

Arguments are separate strings. Do not pre-quote them or concatenate a command line: ProcessKit
passes the argument list directly to the child.

## Use `CliClient` for a tool you call repeatedly

A `CliClient` keeps one program name and shared `Command` defaults. It is useful for scripts that call
Git, `dotnet`, `ffmpeg`, or another CLI many times.

```fsharp
let git =
    (CliClient.create "git")
        .WithDefaults(fun command ->
            command
                .CurrentDir(Environment.CurrentDirectory)
                .Timeout(TimeSpan.FromSeconds 30.0))

let head =
    git.RunAsync [ "rev-parse"; "HEAD" ]
    |> fun pending -> pending.GetAwaiter().GetResult()

let recent =
    git.OutputStringAsync [ "log"; "--oneline"; "-n"; "5" ]
    |> fun pending -> pending.GetAwaiter().GetResult()

match head, recent with
| Ok sha, Ok log -> printfn $"HEAD {sha}\n{log.Stdout}"
| Error error, _
| _, Error error -> eprintfn $"git failed: {error.Message}"
```

Use `client.Command args` when one invocation needs an extra builder option; the returned immutable
`Command` keeps all client defaults.

## Build pipelines without shell syntax

`Command.Pipe` connects stdout to stdin without invoking `bash`, `cmd.exe`, or PowerShell. There is no
shell quoting, word splitting, wildcard expansion, or injection surface, and ProcessKit contains the
whole chain in one process group.

```fsharp
let authors =
    (Command.create "git" |> Command.args [ "log"; "--format=%an" ])
        .Pipe(Command.create "sort")

match authors.OutputStringAsync().GetAwaiter().GetResult() with
| Ok result -> printf "%s" result.Stdout
| Error error -> eprintfn $"pipeline failed: {error.Message}"
```

Shell operators are not arguments: `>`, `2>&1`, `|`, `&&`, `$VAR`, and `*.fs` have no special meaning.
Use the corresponding ProcessKit builder (`Stdout`, `MergeStderr`, `Pipe`, environment builders) or
expand files in F# before creating the command.

## Handle Ctrl+C without orphaning the run

For a live handle, `ForwardParentSignals` installs and owns the signal handlers, suppresses the
parent's immediate termination, and forwards the first request into the run's graceful
`StopSignal` → grace → hard-kill path. It automatically unregisters when the child exits; disposing
the returned scope unregisters earlier.

```fsharp
let runWithCtrlC () =
    task {
        match! (Command.create "long-running-tool").StartAsync() with
        | Error error -> return Error error
        | Ok running ->
            use running = running
            use _signals = running.ForwardParentSignals(TimeSpan.FromSeconds 5.0)
            return! running.OutputStringAsync()
    }

let childExit = runWithCtrlC().GetAwaiter().GetResult()
```

```csharp
var started = await new Command("long-running-tool").StartAsync();
if (started is { IsOk: true, ResultValue: var running })
{
    await using (running)
    using (running.ForwardParentSignals(TimeSpan.FromSeconds(5)))
        await running.OutputStringAsync();
}
```

On POSIX the scope handles `SIGINT` and `SIGTERM`. On Windows it handles Ctrl+C and Ctrl+Break,
but forwarding means the existing Windows `StopAsync` contract — best-effort `WM_CLOSE`, then atomic
Job termination after the grace window — not a guarantee that a windowless console child receives
the original Ctrl event. Repeated signals while the scope is active do not start duplicate teardown.

Do not call `Environment.Exit`: it bypasses the unwind the forwarding scope protects. A crash,
`SIGKILL`, or `TerminateProcess` cannot run
managed disposal; if sudden parent death is in scope, read
[`KillOnParentDeath`](containers.md#when-the-parent-is-killed-outright-commandkillonparentdeath)
before opting in. Its honest scope is platform-specific: whole tree on Windows, direct child only on
Linux, and unavailable on macOS/BSD.

Completion verbs can instead receive a cancellation token for the whole run and return
`ProcessError.Cancelled`; `ForwardParentSignals` is specifically for a caller-owned live handle where
you want an honest graceful `Outcome`.

## Return an honest exit code

`ExitCodeAsync` returns the child's real code. A timeout or signal is an error instead of a made-up
sentinel; cancellation can be mapped to the conventional `130`. Set `Environment.ExitCode` only after
ProcessKit has finished cleanup.

```fsharp
let publishExitCode (childExit: Result<int, ProcessError>) =
    let scriptExitCode =
        match childExit with
        | Ok code -> code
        | Error(ProcessError.Cancelled _) -> 130
        | Error error ->
            eprintfn $"run failed: {error.Message}"
            1

    Environment.ExitCode <- scriptExitCode
```

Call `publishExitCode childExit` with the result from the previous section.

When you also need stdout and stderr, use `OutputStringAsync`: its `Ok result` carries `Code`,
`Outcome`, `Stdout`, and `Stderr`, including for a non-zero exit. Only failure to start or drive the
child is `Error` on that capture path.

## Resolve tools portably

Choose the preflight that answers the question you actually have:

- `Exec.which "git"` and `CliClient.EnsureAvailableAsync()` inspect the host process's `PATH`.
- `Command.ResolveProgram()` resolves the command's effective child `PATH`, including `Env`,
  `EnvClear`, and `PreferLocal`.
- `CliClient.ResolveProgram()` does the same for the client's configured template.

```fsharp
let formatter =
    (CliClient.create "eslint")
        .WithDefaults(fun command -> command.PreferLocal("node_modules/.bin"))

match formatter.EnsureAvailableAsync().GetAwaiter().GetResult() with
| Ok path -> printfn $"installed on host: {path}"
| Error _ -> printfn "not on the host PATH"

match formatter.ResolveProgram() with
| Ok path -> printfn $"this client will launch: {path}"
| Error error -> eprintfn $"client cannot resolve its tool: {error.Message}"
```

The two answers may legitimately differ when a script supplies its own `PATH` or prefer-local
directory. Resolution is side-effect-free: it never launches the tool.

## Cross-platform checklist

- Keep arguments separate and use `Pipeline` instead of shell strings. A shell built-in is not an
  executable; if you genuinely need shell syntax, invoke that shell explicitly and accept its quoting
  contract.
- Use `System.IO.Path` rather than embedding `/` or `\` in paths you construct.
- Windows executable lookup is `PATHEXT`-aware, so bare tool names resolve consistently through
  `Exec.which`, `ResolveProgram`, and the real spawn path.
- UTF-8 remains the default. For a legacy Windows console program that writes an OEM/console code page,
  add `.ConsoleEncoding()` to the `Command`; it is an unchanged UTF-8 choice off Windows.
- `PreferLocal` paths are resolved against the command's working directory when one is set, otherwise
  against the script process's current directory.
- Treat `Result` explicitly and set `Environment.ExitCode`; an unhandled `ProcessError` printed as text
  is not the same thing as a failing script.

---

Next: [Running commands](commands.md)
