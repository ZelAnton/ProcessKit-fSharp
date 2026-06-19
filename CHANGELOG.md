# Changelog

All notable changes to **ProcessKit** are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added
- Core run vocabulary: an immutable `Command` builder, the `IProcessRunner` seam, and the `Runner` verbs `run`/`runUnit`/`outputString`/`outputBytes`/`exitCode`/`probe`, returning `Task<Result<_, ProcessError>>` (a non-zero exit is data, not an error).
- `ProcessResult<'T>`, `Outcome`, `Mechanism`, and the structured `ProcessError` failure type.
- `ProcessKit.Testing.ScriptedRunner` and `Reply`: a subprocess-free `IProcessRunner` for hermetic tests.
- `ProcessGroup`, a kill-on-dispose container for a process tree, and `JobRunner`, the default real-process `IProcessRunner`. Windows containment uses a Job Object (`KILL_ON_JOB_CLOSE`) with an atomic suspended-spawn → assign → resume so no descendant can escape; Linux/macOS use a POSIX process group (`posix_spawn` with `POSIX_SPAWN_SETPGROUP`, `killpg` teardown). The tree is reaped on dispose or GC finalization, and the active `Mechanism` is reported honestly.
- `ProcessGroup.Shutdown(gracePeriod)`: graceful teardown — SIGTERM then SIGKILL after the grace period on Unix, an atomic Job kill on Windows — releasing the group when done.
- Streaming & interactive I/O: `IProcessRunner.Start` / `Command.Start()` return a live `RunningProcess` with `StdoutLines()` / `OutputEvents()` (as `IAsyncEnumerable`), `Wait`/`OutputString`/`OutputBytes`/`Finish`, `TakeStdin`, `StartKill`, `Pid`/`Elapsed`/`StartTime`, and kill-on-dispose (`IAsyncDisposable`).
- `Stdin` input sources (`FromString`/`FromBytes`/`FromFile`/`FromReader`/`FromIterLines`/`FromLines`/`Empty`) and the interactive `ProcessStdin` handle; `OutputLine`, `OutputEvent`, `Finished`.
- Per-stream `Command` builders: `Stdin`/`KeepStdinOpen`, `Stdout`/`Stderr` (`StdioMode` Piped/Inherit/Null), `StdoutEncoding`/`StderrEncoding`/`Encoding`, `OnStdoutLine`/`OnStderrLine`, `StdoutTee`/`StderrTee`, and `OutputBuffer` (`OutputBufferPolicy` with line/byte caps and `OverflowMode`).
- `ProcessError.OutputTooLarge` (fail-loud output ceiling) and `ProcessError.Stdin`.

### Changed
-

### Fixed
-

[Unreleased]: https://github.com/ZelAnton/ProcessKit-fSharp/commits/main
