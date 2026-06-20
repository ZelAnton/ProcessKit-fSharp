# ProcessKit (F#) — porting roadmap

This repository is an F#/.NET port of the Rust crate
[**ProcessKit-rs**](https://github.com/ZelAnton/ProcessKit-rs). We port its
**complete, stable `1.0.1`** surface — feature-complete and SemVer-frozen upstream —
**one feature at a time**, finishing and verifying each stage before the next begins.

> **What this is.** The committed plan and the order of work. It is not a literal
> transliteration of the Rust source: we reproduce the *observable contract*, not the
> Rust *mechanism*. See [How we port](#how-we-port).

## How we port

- **One feature at a time, gated by confirmation.** Each stage is taken to a green
  build, green tests, clean Fantomas, and a `CHANGELOG.md` entry. Then work **stops**
  until the maintainer confirms the stage is correctly implemented. Only then does the
  next stage begin. The core (Phase 1) is large, so it is split into milestones that
  are each individually green and reviewable.
- **Faithful behaviour, idiomatic form.** What must match upstream: the
  *unconditional* kill-on-drop tree guarantee; the verb vocabulary and its meanings;
  honest results (a non-zero exit is data, not a raised error, until success is
  requested); typed-or-documented platform divergence (never a silent downgrade). The
  implementation mechanism is free to be the idiomatic .NET one.
- **Quality bar, every stage:** reliable, readable, fully testable, extensible —
  written for a consumer who will use, debug, maintain, and extend it.
- **Testability is designed in, not added.** Every layer is reachable through the
  `IProcessRunner` seam with subprocess-free doubles, mirroring the Rust crate's
  hermetic test story.

## Design translation (Rust → F#/.NET)

The reasons many Rust constructs exist (manual `Drop`, `cargo` feature gates, a
hand-rolled `CancellationToken`) are already solved primitives on .NET. The port uses
the .NET primitive rather than reimplementing the Rust one.

| Concern | ProcessKit-rs | ProcessKit (F#) |
|---|---|---|
| Async | tokio `async fn` / futures | **`System.Threading.Tasks.Task<'T>`** (ratified), implemented with `task { }` |
| Streaming | `futures::Stream` (`StdoutLines`, `OutputEvents`, `StatsSampler`) | `IAsyncEnumerable<'T>` (`[<EnumeratorCancellation>]`) |
| Cancellation | `tokio_util::sync::CancellationToken` + `Command::cancel_on` | first-class `System.Threading.CancellationToken` on every async verb; a cancelled run is always an error |
| Kill-on-drop | `Drop` (deterministic destructor) | `IAsyncDisposable`/`IDisposable` — deterministic under `use`/`use!`; finalizer + OS close-to-kill (Job Object `KILL_ON_JOB_CLOSE`, cgroup teardown) as the GC-time safety net |
| Errors | `enum Error` (`#[non_exhaustive]`) + `Result<T>` | a structured, pattern-matchable `Error` DU + **`Result<'T, Error>`** (ratified); verbs return `Task<Result<'T, Error>>`; honest-result verbs return their value (a non-zero exit is `Ok` data) |
| Runner seam | `ProcessRunner` trait, `JobRunner`, `ProcessRunnerExt` | `IProcessRunner` interface (3 core methods) + extension/module verbs; this *is* the DI seam and the test seam |
| Test doubles | `ScriptedRunner`, `RecordingRunner`, `RecordReplayRunner`, `mock` feature (`mockall`) | hand-written `ScriptedRunner`/`RecordingRunner` over `IProcessRunner`; **`mock` feature → N/A** (mock the interface with any .NET mock framework) |
| Observability | `tracing` feature | optional `Microsoft.Extensions.Logging.ILogger` (Abstractions only, `NullLogger` default); same events; never logs argv/env |
| Encoding | `encoding_rs::Encoding` | `System.Text.Encoding` (note: register `CodePagesEncodingProvider` for legacy code pages) |
| Builder | consuming `self` builders | immutable `Command` value with fluent methods returning a new instance (+ an F# module for pipe-style) |
| Containment | Job Object / cgroup v2 / POSIX pgroup via `windows-sys` / syscalls | Job Object via Win32 P/Invoke; cgroup v2 via the cgroup filesystem; POSIX pgroup via libc P/Invoke. Spawn likely needs a custom suspended-create+assign path, not bare `System.Diagnostics.Process`, for atomic containment |
| Packaging | additive `cargo` *visibility* features | **one NuGet package, full surface**; "features" become modules. Optional `ProcessKit.Extensions.DependencyInjection` for container wiring |
| Free fns / macros | `processkit::run`, `wait_any`, `cli_client!` | module functions; `cli_client!` macro → revisit as a source generator or omit |

### Ratified decisions

- **Error channel — `Result<'T, Error>`.** Verbs return `Task<Result<'T, Error>>`;
  `Error` is a structured DU callers pattern-match. Honest-result verbs still return
  their value (a non-zero exit is `Ok` data, not an `Error`).
- **Public async type — `Task<'T>`.** Implemented with `task { }`; streams are
  `IAsyncEnumerable<'T>`; every async verb takes an explicit `CancellationToken`; the
  `IProcessRunner` seam returns `Task<…>` so a C# consumer can `await`, implement, and
  mock it natively. Chosen over `Async<'T>` because the project must drop into C#
  solutions: `Async` surfaces in C# as `FSharpAsync<'T>` (needs `StartAsTask`, leaks
  FSharp.Core into call sites), stacking a second layer of F# friction on top of the
  F# `Result`; `Task` is the .NET async lingua franca and matches the substrate we
  build on (`Process.WaitForExitAsync(ct)`, `Stream.ReadAsync`, `IAsyncEnumerable`).
  The `Command` value stays the cold description; the verb call is where work goes hot.

### Open decisions

1. **Cancellation — value, not throw.** Faithful to Rust ("a cancellation is always an
   error"), a cancelled run resolves to `Error.Cancelled` *inside* the `Result` — the
   `Task` completes, it is not faulted/cancelled. Recommended (consistent with the
   errors-as-values channel); trade-off is that C# can't `catch
   (OperationCanceledException)` for it, by design. Programmer misuse (e.g. using a
   disposed handle) still throws.
2. **Internal `Task<Result<>>` plumbing.** A hand-rolled minimal `taskResult` CE (no new
   dependency) vs. taking `FsToolkit.ErrorHandling`. Internal only — no public-surface
   impact; leaning hand-rolled to keep the core dependency-light.
3. **Containment scope for M1.1** — Windows Job Object + POSIX process group together
   *(recommended: minimally covers all three CI OSes; Linux cgroup v2 preferred path
   lands in M1.2)* vs. one platform first vs. all three (incl. cgroup v2) up front.

---

## Phase 1 — Core (the default build): the kill-on-drop runner

Everything a default `cargo build` of ProcessKit-rs exposes — the unconditional core
plus the default `process-control` feature. This is the bulk of the library and the
foundation everything else layers onto. Split into individually-green milestones; the
maintainer reviews at each milestone boundary, with the formal feature gate at the end
of the phase.

- **M1.1 — Foundations + walking skeleton.** `Error`, `Outcome`, `ProcessResult<'T>`,
  `Mechanism`; a first slice of `Command` (program/args/cwd/env); `ProcessGroup`
  containment (Windows Job Object + POSIX process group); the `IProcessRunner` seam
  with the default runner; the core verbs `run` / `run_unit` / `output_string` /
  `output_bytes` / `exit_code` / `probe`; and the `ScriptedRunner` double. Proves the
  whole architecture end-to-end: spawn into a kernel container, capture, honest
  results, the verb vocabulary, the mock seam, kill-on-dispose.
- **M1.2 — Graceful teardown + containment honesty.** `ProcessGroup.shutdown` —
  graceful SIGTERM → grace window → SIGKILL on Unix, atomic on Windows; honest
  `Mechanism` reporting; and a Linux test that proves the documented `setsid`-escape
  weakness of a POSIX process group (a `setsid` child leaves the group). *(The Linux
  **cgroup v2** preferred path is re-sequenced to the `limits` feature — see S3: it is
  delegation/privilege-gated so it never runs in unprivileged CI, and atomic placement
  needs `clone3(CLONE_INTO_CGROUP)` rather than `posix_spawn`; it lands where it is
  actually required and validated together. The POSIX process group remains the Linux
  mechanism until then.)*
- **M1.3 — Streaming & interactive I/O.** `Command::start` → `RunningProcess`;
  `stdout_lines` / `output_events` as `IAsyncEnumerable`; `Stdin` / `ProcessStdin`
  (string/bytes/file/reader/lines); `on_stdout_line` / `on_stderr_line` callbacks;
  `stdout_tee` / `stderr_tee`; `OutputBufferPolicy` + `OverflowMode` (incl.
  `fail_loud` → `OutputTooLarge`); per-stream `StdioMode` and encoding.
- **M1.4 — Readiness & racing.** `wait_for_line` / `wait_for_port` / `wait_for`;
  `wait_any` / `wait_all`; `finish` / `Finished`.
- **M1.5 — Timeouts, cancellation, retry.** `timeout` / `timeout_grace` /
  `timeout_signal`; `CancellationToken` wiring end-to-end; `retry`; per-run graceful
  shutdown. A timeout is captured in the result where Rust captures it; a cancellation
  is always an error.
- **M1.6 — Pipelines.** `Pipeline`, `Command::pipe` (and the `|` operator analog),
  one shared group, pipefail outcome, `unchecked_in_pipe`. *(Done — `Command.Pipe` →
  `Pipeline` with the full verb set plus `Timeout`/`CancelOn`, all stages in one shared
  kill-on-dispose group, stages wired stdout→stdin with **no shell**. Pipefail = the
  rightmost **checked** stage that did not exit 0 (else the last checked stage), with
  `Command.UncheckedInPipe()` opting a stage out. The `|` operator is **not** ported —
  F# reserves `|`; the fluent `Pipe` / `Pipeline.create` / `Pipeline.pipe` cover it. An
  early-exiting consumer closes the upstream read end so the producer gets a broken pipe
  (SIGPIPE on POSIX) instead of blocking — Windows has no SIGPIPE, so that guarantee is
  POSIX-only.)*
- **M1.7 — Supervision.** `Supervisor` with `RestartPolicy`, backoff + `max_backoff` +
  `jitter`, `max_restarts`, `failure_threshold` / `failure_decay`, `storm_pause`,
  `stop_when` → `SupervisionOutcome` / `StopReason`. *(Done — built entirely on the
  `IProcessRunner` seam (default `JobRunner`, override with `WithRunner`), so supervision
  is hermetically testable. Crash = not `ProcessResult.IsSuccess` (exit 0); the
  `ok_codes`-aware refinement is **deferred** (it is cross-cutting across `Command` /
  `ProcessResult` / the verbs) and the supervisor picks it up automatically once it lands,
  since classification routes through `IsSuccess`. Backoff/storm timing uses an internal
  injectable clock seam — real time by default, a virtual advance-on-sleep clock in tests
  — so the timing suite is deterministic and instant.)*
- **M1.8 — Tree control + ergonomics.** `Signal` and
  `ProcessGroup::{signal, suspend, resume, members, adopt}` (the default
  `process-control` feature); sandbox knobs (`inherit_env`, `uid`/`gid`/`groups`,
  `setsid`, `create_no_window`, `keep_stdin_open`, `kill_on_parent_death`);
  `CliClient` + `IntoCommand`; batch `output_all` / `output_all_bytes`; top-level
  `run` / `output_string`. Optional `ILogger` plumbing wired through the lifecycle.
  *(Done, in two parts. **M1.8a (process control):** the `Signal` type and
  `ProcessGroup.Signal`/`Suspend`/`Resume`/`Members`/`TerminateAll`; `ProcessGroup`
  became an `IProcessRunner` (shared-group `Start`/`OutputString`/`OutputBytes`),
  which completes `Supervisor.WithRunner(group)`; and a fix so POSIX reaps every
  child of a multi-child group (each `posix_spawn` forms its own pgid). **M1.8b
  (ergonomics):** `Command.OkCodes` (now `ProcessResult.IsSuccess`/`ensureSuccess`/
  `Supervisor` honour accepted exit codes), `CreateNoWindow`, `InheritEnv`,
  `CliClient`, top-level `Exec.run`/`outputString`, and bounded-concurrency
  `Exec.outputAll`/`outputAllBytes`. **Deferred (tracked):** `adopt` and the
  `uid`/`gid`/`groups`/`setsid`/`kill_on_parent_death` sandbox knobs all need
  `fork`+`exec` (not `posix_spawn`) and are privilege/platform-gated, so they are
  untestable in unprivileged CI; `IntoCommand` + the `cli_client!` macro are
  Rust-isms; and the optional `ILogger` plumbing lands in **S5** (its dedicated
  observability milestone).)*

→ **Phase 1 confirmation gate.**

## Phase 2 — Opt-in features (post-core)

Each is the F# analog of a non-default Rust feature, layered into the same package as
a module. One stage, one confirmation gate.

- **S2 — `stats`.** `ProcessGroupStats`, `ProcessGroup::stats`, `sample_stats`
  (`StatsSampler` as `IAsyncEnumerable`), per-process `cpu_time` / `peak_memory_bytes`,
  and `RunProfile` / `RunningProcess::profile`. Windows peak-memory readout via PSAPI
  P/Invoke (built-in, no package).
- **S3 — `limits` (+ Linux cgroup v2).** `ResourceLimits` and the `memory_max` /
  `max_processes` / `cpu_quota` builders on `ProcessGroupOptions`; `ResourceLimit`
  failure. This brings the **Linux cgroup v2 preferred containment path** (the cgroup
  controllers it needs require it): atomic placement via `clone3(CLONE_INTO_CGROUP)`,
  `cgroup.kill` teardown, `Mechanism.CgroupV2`, with the POSIX-pgroup fallback when
  cgroup delegation is unavailable. Validated via a `--privileged` container — cgroup v2
  is delegation-gated, so the unprivileged CI legs exercise the pgroup fallback. Job
  Object limits on Windows. *(Done — `ProcessGroup.Create(options)` with
  `ResourceLimits` (`MemoryMax`/`MaxProcesses`/`CpuQuota`); `ProcessError.ResourceLimit`.
  Linux cgroup v2 (`Mechanism.CgroupV2`) is the limits backend — **used when limits are
  requested**; without limits Linux stays on the pgroup mechanism, so nothing regresses.
  Placement is **`cgroup.procs` migration** after `posix_spawn` (the same mechanic the
  Rust crate uses — `clone3(CLONE_INTO_CGROUP)` was the plan note, not what upstream does),
  with `cgroup.kill` teardown, `cgroup.freeze` suspend/resume, and `cpu.stat`/`memory.peak`
  feeding `Stats`. Windows Job-Object limits (`JobMemoryLimit` / `ActiveProcessLimit` /
  CPU hard cap). Fail-fast `ResourceLimit` on macOS and where the cgroup is not the real
  root. A **privileged CI leg** (`--privileged --cgroupns=host`, process moved to the real
  cgroup root) exercises real cgroup enforcement; the unprivileged matrix legs exercise the
  fail-fast path.)*
- **S4 — `record`.** Record/replay cassettes over `IProcessRunner`
  (`RecordReplayRunner`) using `System.Text.Json` (built-in); `Invocation` capture,
  hermetic replay, `CassetteMiss`. *(Done — `ProcessKit.Testing.RecordReplayRunner`
  (`Record`/`Replay`/`Save` + drop-time flush) with `System.Text.Json` cassettes;
  `ProcessError.CassetteMiss`; matching on program+args+cwd+stdin-digest with
  order-then-repeat-last duplicates; covers `OutputString`/`OutputBytes`, `Start`
  unsupported. Cassettes are `0600` on Unix and redact env values. One-shot stdin
  sources (`FromReader`/`FromLines`) can't be keyed and error — a slightly tighter
  rule than upstream's eager `from_iter_lines`.)*
- **S5 — Observability parity.** Complete the `ILogger` event taxonomy to match the
  Rust `tracing` feature (spawn/exit, timeout/cancel, teardown anomalies, retries,
  supervisor restarts/storm pauses) — argv/env never logged.
- **S6 — DI extensions.** A separate `ProcessKit.Extensions.DependencyInjection`
  package: `AddProcessKit` registering `IProcessRunner` (and an `ILogger`-aware runner)
  for `Microsoft.Extensions.DependencyInjection` consumers.

## Known limitations (tracked)

Surfaced by the milestone reviews; deferred deliberately because they are internal
(invisible to the frozen `Task` API) or are documented constraints, not correctness bugs:

- **Blocking waits.** `waitWindows`/`waitPosix` block a thread-pool thread per running process
  (Windows anonymous pipes are not overlapped, so reads are sync-over-async too). Under heavy
  concurrency (large `WaitAll`, `Supervisor`, `output_all`) this pressures the thread pool. The
  public API would not change when the internals move to overlapped/registered waits — a
  dedicated async-I/O hardening pass owns this.
- **One terminal consumption per `RunningProcess`.** `WaitForLine` → `StdoutLines` → `Finish`
  compose; `OutputString`/`OutputBytes`/`Wait` are standalone. Mixing terminal verbs (e.g.
  `OutputString` *and* `StdoutLines`) double-pumps the same pipe — documented, not yet guarded.
- **Streaming backlog.** A streamed (`StdoutLines`/`OutputEvents`) consumer that stops draining
  while the child floods grows the channel unbounded; the `OutputBufferPolicy` ceiling is applied
  to the *buffered* verbs, and streaming is consumer-paced (pair with a `timeout`).
- **Default UTF-8 decoding.** Captured text is decoded UTF-8 by default; Windows console programs
  emitting a legacy OEM code page need an explicit `StdoutEncoding`. (Matches upstream.)
- **POSIX pgid-reuse window.** The process-group teardown has a small reuse window on Unix; cgroup
  v2 (in the `limits` feature, S3) closes it.
- **Privileged sandbox knobs and `adopt` deferred.** `ProcessGroup.adopt` and the
  `uid`/`gid`/`groups`/`setsid`/`kill_on_parent_death` knobs need `fork`+`exec` (a child `pre_exec`
  hook) rather than `posix_spawn`, and are privilege/platform-gated, so they cannot run in
  unprivileged CI. They are additive when added later. (`create_no_window`, `inherit_env`, and
  `keep_stdin_open` shipped in M1.8; `ok_codes` shipped and is honoured by `IsSuccess`/`ensureSuccess`/
  `Supervisor`.)

## Out of scope / deferred

- **`mock` feature** — no equivalent; the `IProcessRunner` interface is the mock seam.
- **`cli_client!` macro** — Rust-macro-specific; revisit as a Roslyn source generator
  or omit in favour of the plain `CliClient` type.
- Upstream `later-*` ideas (PTY, runtime-agnostic split, detached handoff, …) stay
  deferred exactly as they are upstream.

---

> Progress, behavioural notes, and any deliberate divergence from upstream are recorded
> in `CHANGELOG.md` (the release-notes source of truth) as each stage lands.
