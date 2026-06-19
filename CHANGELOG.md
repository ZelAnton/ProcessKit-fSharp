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

### Changed
-

### Fixed
-

[Unreleased]: https://github.com/ZelAnton/ProcessKit-fSharp/commits/main
