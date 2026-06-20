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
- `Stdin` input sources (`FromString`/`FromBytes`/`FromFile`/`FromReader`/`FromLines`/`FromAsyncLines`/`Empty`) and the interactive `ProcessStdin` handle; `OutputLine`, `OutputEvent`, `Finished`.
- Per-stream `Command` builders: `Stdin`/`KeepStdinOpen`, `Stdout`/`Stderr` (`StdioMode` Piped/Inherit/Null), `StdoutEncoding`/`StderrEncoding`/`Encoding`, `OnStdoutLine`/`OnStderrLine`, `StdoutTee`/`StderrTee`, and `OutputBuffer` (`OutputBufferPolicy` with line/byte caps and `OverflowMode`).
- `ProcessError.OutputTooLarge` (fail-loud output ceiling) and `ProcessError.Stdin`.
- `ProcessError.Message` — a short human-readable description (also its `ToString`), for logging and diagnostics.
- `ProcessError.isNotFound` / `ProcessError.isTransient` — classifiers for error handling (e.g. retriable spawn/I/O failures vs a missing program).
- Readiness probes on `RunningProcess`: `WaitForLine` (await a matching stdout line), `WaitForPort` (await a TCP port), `WaitFor` (poll a custom predicate); `ProcessError.NotReady` on timeout.
- `RunningProcess.WaitAny` / `WaitAll` to race or await several started processes.
- `Command.Timeout` / `TimeoutGrace` (kill the run at a deadline, reporting `Outcome.TimedOut`; graceful = SIGTERM→grace→SIGKILL on Unix, atomic on Windows), `Command.CancelOn` (tie a run to a `CancellationToken`), and `Command.Retry` (re-run on a retriable error); `ProcessError.NotReady` joined by per-run timeout handling.
- Shell-free pipelines: `Command.Pipe(next)` builds a `Pipeline` that wires each stage's stdout into the next stage's stdin, running the whole chain in one kill-on-dispose group. The same verbs as a single command (`Run`/`RunUnit`/`OutputString`/`OutputBytes`/`ExitCode`/`Probe`/`Parse`/`TryParse`) plus `Pipeline.Timeout`/`CancelOn`. The exit status follows shell **pipefail** (the rightmost checked stage that did not exit 0 decides the result); `Command.UncheckedInPipe()` lets a stage fail without failing the pipeline.
- Completed the `Command` convenience verbs: `RunUnit`/`ExitCode`/`Probe`/`Parse`/`TryParse`/`FirstLine` now sit alongside `Run`/`OutputString`/`OutputBytes` (all on the default runner), and every convenience verb has a `CancellationToken` overload.
- `Supervisor` — keep a command alive with policy-driven restarts: `RestartPolicy` (`Always`/`OnCrash`/`Never`), exponential `Backoff` + `MaxBackoff` + `Jitter`, `MaxRestarts`, a failure-storm guard (`StormPause` + `FailureThreshold` + `FailureDecay`), and a `StopWhen` predicate, reporting a `SupervisionOutcome` (`FinalResult`/`Restarts`/`Stopped`/`StormPauses`) with `StopReason`. Runs through any `IProcessRunner` (`WithRunner`) so supervision is testable without spawning processes.
- Process-tree control on `ProcessGroup`: `Signal` (the portable `Signal` type — `Term`/`Kill`/`Int`/`Hup`/`Quit`/`Usr1`/`Usr2`/`Other`; Windows delivers only `Kill`), `Suspend`/`Resume` (freeze/thaw the whole tree), `Members` (a pid snapshot), and `TerminateAll`.
- `ProcessGroup` now implements `IProcessRunner` (`Start`/`OutputString`/`OutputBytes`): every run goes into that one shared kill-on-dispose group, so a fleet can share a container — e.g. `Supervisor.WithRunner(group)`. `ProcessGroup.Start` returns a `RunningProcess` whose lifetime the group owns.
- `Command.OkCodes(codes)` — treat the given exit codes (in addition to `0`) as success, widening `ProcessResult.IsSuccess` (also exposed as `AcceptedCodes`), `ensureSuccess`, the `Run` verbs, and `Supervisor` crash classification. `Command.CreateNoWindow()` (Windows `CREATE_NO_WINDOW`); clear the child's environment with `Command.EnvClear()`.
- `CliClient` — a reusable handle to one program with shared defaults (timeout, environment, working directory, cancellation): build configured `Command`s for argument lists, or run them through the client's runner.
- Top-level `Exec` conveniences: `Exec.run` / `Exec.outputString` (run a program by name), and `Exec.outputAll` / `Exec.outputAllBytes` (run a batch of commands with bounded concurrency, collecting every result in input order).
- Resource stats: `ProcessGroup.Stats` (a `ProcessGroupStats` snapshot — active process count, plus total CPU time and peak memory on Windows; CPU/memory are `None` on the POSIX fallback) and `ProcessGroup.SampleStats(interval)` (a periodic `IAsyncEnumerable` series). Per-process `RunningProcess.CpuTime` / `PeakMemoryBytes`, and `RunningProcess.Profile` returning a `RunProfile` (exit code, duration, CPU, peak memory, sample count, and `AvgCpu`).
- Whole-tree resource limits: `ResourceLimits` / `ProcessGroupOptions` (`WithMemoryMax` / `WithMaxProcesses` / `WithCpuQuota`) applied via `ProcessGroup.Create(options)`. Enforced by a **Windows Job Object** or a **Linux cgroup v2** (`Mechanism.CgroupV2`, with `cgroup.kill` teardown and cgroup accounting in `Stats`); when no limit-capable container is available (macOS, or a Linux cgroup that is not at the real root), creation fails fast with `ProcessError.ResourceLimit` rather than leaving the tree unbounded. `ProcessGroupStats.TotalCpuTime` / `PeakMemoryBytes` are now populated on the cgroup v2 backend.
- `ProcessKit.Testing.RecordReplayRunner` — an `IProcessRunner` that records real runs to a JSON cassette (`Record(path, inner)` / `Save`, with a drop-time flush) and replays them hermetically (`Replay(path)`) with no subprocess. Matches on program + args + cwd + stdin-source digest; an unmatched call is `ProcessError.CassetteMiss`. Covers `OutputString`/`OutputBytes`; cassettes are owner-only (`0600`) on Unix and store env *names* only (values redacted).
- Optional `Microsoft.Extensions.Logging` integration: `Command.WithLogger(logger)` emits structured lifecycle events — spawn, exit, timeout, retry, and supervisor restart / failure-storm pause. **argv and the environment are never logged** (only the program name and non-secret facts). No-op and free when no logger is set.
- New `ProcessKit.Extensions.DependencyInjection` package: `IServiceCollection.AddProcessKit()` registers `IProcessRunner` (a singleton `JobRunner`), wrapped to emit ProcessKit's lifecycle events when the container has an `ILoggerFactory`. Uses `TryAdd`, so a pre-existing `IProcessRunner` registration is left untouched.

### Changed
-

### Fixed
- POSIX containment now reaps **every** child of a multi-child group (e.g. a pipeline), not just the last. Each `posix_spawn` forms its own process group, so the group tracks all of them; previously only the most-recent pgid was killed, letting an earlier long-running stage linger until its natural exit.
- A throwing `OnStdoutLine`/`OnStderrLine` handler now surfaces the error on `StdoutLines()` / `OutputEvents()` (and `Finish()`) instead of hanging the stream reader: the output channel is always completed — carrying the fault — even when a line pump throws. Across both the streaming verbs and the capture verbs (`OutputString`/`OutputBytes`) the two output pumps are now awaited together, so a fault in one never leaves the other running as an unobserved task.
- Every terminal `RunningProcess` verb (`OutputString`/`OutputBytes`/`Wait`/`Profile`/`Finish`) now reaps the process tree even when the run faults mid-flight (e.g. a throwing line handler), rather than deferring teardown to disposal/GC.
- `RunningProcess.Profile` now cancels and awaits its background metric sampler on the fault path too — a fault mid-profile can no longer leave the sampler running against a soon-to-be-disposed token as an unobserved task.

[Unreleased]: https://github.com/ZelAnton/ProcessKit-fSharp/commits/main
