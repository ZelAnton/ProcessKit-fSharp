# ProcessKit — post-2.0 hardening & enhancement plan

> **Internal engineering plan, not a consumer guide.** This document expands the
> "Tracked limitations" and "Future directions" of [`ROADMAP.md`](../../ROADMAP.md)
> into actionable work items. It is deliberately kept out of the consumer-facing
> [`docs/`](../README.md) guide index. Shipped changes still land in
> [`CHANGELOG.md`](../../CHANGELOG.md); this file plans work that has **not** shipped.

Each item below states **what to do**, **why**, and **acceptance criteria**, grounded
in the current implementation (file/type/line references are the seams to touch, as of
this writing — re-confirm before editing). Three of the five items (**ILogger**, **DI
Extensions**, **Record/replay**) already have a shipped baseline; those sections plan
the *next increment*, not a from-scratch build.

## Cross-cutting rules (apply to every item)

These come from the repository's [`CONTRIBUTING.md`](../../CONTRIBUTING.md), the porting
methodology in [`ROADMAP.md`](../../ROADMAP.md), and the repo's agent-instruction docs; every
item's "definition of done" includes them, so they are stated once here rather than repeated:

- **One feature at a time, then stop.** Take a stage to green build + tests + Fantomas +
  `CHANGELOG.md` entry, then **wait for the user to confirm** before starting the next.
  Do not run ahead.
- **Port the contract, not the form.** Where a counterpart exists in the Rust crate
  `ProcessKit-rs`, match the *observable contract and vocabulary* (verb meanings, honest
  results, typed/documented platform
  divergence — never a silent downgrade) and implement it with the idiomatic .NET
  mechanism. Do **not** transliterate Rust.
- **Green everywhere.** Build is warnings-as-errors. Tests must pass on the CI matrix
  (`ubuntu-latest`, `windows-latest`, `macos-latest`) and via `scripts/test-linux.ps1`.
  Native/platform-divergent work (items 1, parts of 3/5) is not "done" until it is proven
  on all three OSes, not just the dev box.
- **Guard the public surface.** The public-API snapshot test must stay green — an
  intentional surface change updates the approved `PublicApi.*.approved.txt`; an internal
  change (item 1) must not move it at all.
- **F# specifics.** Compile order in the `.fsproj` is dependency order (insert new files
  after their dependencies); indent with spaces; Fantomas is the style authority; exception
  handlers follow the multi-line / justified-swallow rule.
- **Changelog discipline.** Every user-visible change ships its `CHANGELOG.md` entry in the
  same change set. A pure-internal item with no observable behavior change (item 1) is the
  only kind that may be exempt — but a *perf* change that consumers can measure still gets a
  `### Fixed`/`### Changed` bullet.
- **Never log or store secrets.** argv and environment **values** are never emitted to a
  logger, a trace tag, a metric tag, or a cassette. This invariant extends to every new
  signal introduced below.

## Suggested sequencing & priority

| # | Item | API impact | Risk | Leverage | Depends on |
|---|------|-----------|------|----------|-----------|
| 1 | Async-I/O hardening | none (internal) | high (native, per-OS) | high (scale) | — |
| 2 | Streaming backlog | small additive | medium | medium | — |
| 3 | ILogger enhancements | additive | low→medium | medium | — |
| 4 | DI Extensions enhancements | additive | low | medium | 3 (to wire metrics/tracing) |
| 5 | Record/replay enhancements | additive | low→medium | high (test reach) | — |

**Recommended order:** start with the two highest-leverage, mostly-independent items —
**1 (async-I/O)** for scale and **5 (record/replay)** for test reach — since they are
self-contained. **3 (ILogger)** splits into quick wins (EventIds, `LoggerMessage.Define`,
correlation) and a larger optional step (tracing/metrics); do the quick wins early because
**4 (DI)** wants to wire the metrics/tracing sources through the container. **2 (streaming)**
carries one product decision (change the default vs opt-in) — settle that before coding.

---

## 1. Async-I/O hardening (overlapped reads on Windows, pidfd/reaper on Unix)

### Current state

- **Windows exit-wait is already event-driven.** `Native.waitWindows` (`src/ProcessKit/Native.fs:373`)
  completes a `TaskCompletionSource<Outcome>` from a thread-pool **registered wait**
  (`ThreadPool.RegisterWaitForSingleObject`, `Native.fs:404`) over a duplicated process
  handle — one pool wait thread serves ~63 handles. No further work needed here.
- **POSIX exit-wait is not.** `Native.waitPosix` (`Native.fs:1181`) is `Task.Run` around a
  **blocking `waitpid(pid, &status, 0)`** (with an `EINTR` retry loop). It parks one
  thread-pool thread per child for the child's entire lifetime. No `pidfd`, no `signalfd`,
  no shared reaper.
- **Pipe reads look async but aren't overlapped.** The read loop `Pump.readLines`
  (`src/ProcessKit/Pump.fs:87`, `stream.ReadAsync` at line 106-107) is written against
  `ReadAsync`, but the underlying streams are opened **without** overlapped/async I/O:
  Windows uses `AnonymousPipeServerStream` (created in `spawnWindowsCore`, `Native.fs:656`,
  `setupOut` at 666-685); POSIX wraps the read fd in a plain `FileStream` (`Native.fs:1391-1392`)
  with no `FileOptions.Asynchronous`. So each `ReadAsync` falls back to a thread-pool
  **blocking** read under the hood — one parked pool thread per piped stream.
- **No overlapped / pidfd infrastructure exists.** No `FILE_FLAG_OVERLAPPED`, no
  `NamedPipeServerStream`, no `pidfd_open`/`signalfd`. All P/Invoke lives in the `internal
  Native` module. The wait seam is `IContainmentBackend.Wait: nativeint -> Task<Outcome>`
  (`Backend.fs:79`), implemented per backend at `Backend.fs:135/205/280`.

### What to do

Two independent sub-tasks, each behind an `internal` seam. Ship them separately.

**(A) Windows overlapped named-pipe reads.** In `spawnWindowsCore` (`Native.fs:656`,
`setupOut` 666-685, and the stdin pipe at 662), replace `AnonymousPipeServerStream` with an
async-capable pipe — a `NamedPipeServerStream` created with `PipeOptions.Asynchronous` (a
unique auto-generated pipe name; hand the inheritable client handle to the child), or a
`CreateNamedPipe`/`CreateFile` pair with `FILE_FLAG_OVERLAPPED`. The spawn keeps returning
`Spawned.Stdout`/`Stderr` as `Stream option` (`Native.fs:23-33`), so `Pump.readLines` and all
consumers are untouched. A true overlapped `ReadAsync` completes via the I/O completion port —
no pool thread parked per stream. Watch handle inheritance carefully (`HANDLE_FLAG_INHERIT` /
security attributes) so exactly the child end is inherited and no handle leaks.

**(B) POSIX event-driven exit-wait.** Replace the `Task.Run` + blocking `waitpid` in
`waitPosix` (`Native.fs:1181`) with:

- **Linux ≥ 5.3:** `pidfd_open(pid, 0)` → wait for the pidfd to become readable (epoll, or an
  awaitable wrapper), then `waitpid(pid, &status, WNOHANG)` to reap and decode. No thread
  parked. Add the `pidfd_open` P/Invoke to the POSIX block in `Native.fs`.
- **Fallback (older Linux, macOS — no pidfd):** a single shared SIGCHLD reaper loop that
  `waitpid(-1, …, WNOHANG)`s and completes per-pid `TaskCompletionSource`s from a pid→TCS
  registry — one reaper for the whole process, instead of one blocked thread per child.
  (macOS alternative: `kqueue` `EVFILT_PROC`; the shared reaper is simpler and portable, so
  prefer it unless the kqueue path proves necessary.)

Preserve `Spawned.Handle` (the `nativeint` pid) — a pidfd is *supplementary*, not a
replacement. Reconcile with the existing teardown reaper `Native.reapLeader` (`Native.fs:1202`,
used by `PosixReap.leader`, `Backend.fs:32`) and the pgid model so a child is never
double-reaped and no zombie leaks. Handle `EINTR`/`ECHILD` and detect `pidfd_open`'s `ENOSYS`
to fall back.

### Why

The ROADMAP's headline tracked limitation: under heavy concurrency (a large `WaitAllAsync`, a
`Supervisor`, `Exec.outputAll`) today's design parks **one thread-pool thread per piped stream
and per POSIX child** for the process's whole lifetime. That throttles scale and can starve the
thread pool (secondary latency, deadlock-adjacent stalls). Making reads genuinely overlapped and
the POSIX wait event-driven removes the per-child/per-stream thread parking, so a fleet of
hundreds of children scales without exhausting the pool. This is pure internal hardening — the
public API is unchanged.

### Acceptance criteria

- [ ] **No public-API change.** The public-API snapshot test is byte-identical before/after.
- [ ] **Thread-pool pressure removed, measured.** Add a concurrency benchmark (e.g. 200
      concurrent `OutputStringAsync`) and show the busy thread-pool thread count stays roughly
      flat as concurrent-child count grows, instead of climbing ~linearly. Record before/after
      numbers in the PR.
- [ ] **Windows:** child inherits exactly the right pipe end; output stays byte-exact (BOM
      strip / tees unaffected); no handle leak under a load test (assert handle count returns
      to baseline).
- [ ] **POSIX:** exit status decodes correctly (clean exit vs signal vs code); **no zombies**
      after teardown (the existing "no defunct processes" tests stay green); `EINTR`/`ECHILD`
      handled; pidfd closed; reaper fallback engaged when `pidfd_open` returns `ENOSYS`.
- [ ] **Green on all three OSes** (CI matrix + `scripts/test-linux.ps1`), not just the dev box.
- [ ] **Honest reporting preserved.** `Mechanism` and `Outcome` semantics unchanged. (Optional:
      expose pidfd-vs-reaper only as an internal diagnostic; do not add public surface.)
- [ ] Fantomas clean; `CHANGELOG.md` `### Fixed`/`### Changed` bullet (perf: no thread parked
      per stream / per POSIX child).

### Risks & notes

Native code with per-OS divergence is the highest-risk item here. Windows overlapped named
pipes need meticulous handle-inheritance and security-attribute handling. The POSIX pidfd path
must not fight the existing pgid-based teardown/escapee cleanup. Ship (A) and (B) as separate
changes so a regression is bisectable. Cross-check the Rust crate's async runtime for the
observable contract (tokio drives child waits via pidfd on Linux) — match the behavior, build
it the .NET way.

---

## 2. Streaming backlog improvements

### Current state

- Streaming verbs back onto **unbounded** channels created in the `RunningProcess` constructor:
  `Channel.CreateUnbounded<string>` for `StdoutLinesAsync`/`WaitForLineAsync`/`FinishAsync`
  (`RunningProcess.fs:77-78`) and `Channel.CreateUnbounded<OutputEvent>` for
  `OutputEventsAsync` (`RunningProcess.fs:80-81`).
- The writer side always does `channel.Writer.TryWrite line |> ignore`
  (`RunningProcess.fs:505` for stdout, `:593` for events) — it **never awaits the reader**. On
  an unbounded channel `TryWrite` always succeeds, so the internal queue *is* the unbounded
  backlog.
- `OutputBufferPolicy` (`MaxBytes` / `MaxLines` / `OverflowMode`) is consulted **only** inside
  `Pump.LineBuffer` (`Pump.fs:47`), which the **buffered** verbs use (`OutputStringAsync` /
  `OutputBytesAsync`). The streaming path explicitly passes `maxLineLength = None` and applies
  no policy (`RunningProcess.fs:191-198`, with a comment stating streaming is consumer-paced).
- `OverflowMode` today: `DropOldest` (ring/tail, sets `Truncated`), `DropNewest` (drop the new
  line, sets `Truncated`), `Error` (fail-loud → `ProcessError.OutputTooLarge`). No bounded
  channel, no backpressure, no awaited `WriteAsync` anywhere.

### What to do

Add an **opt-in** bounded/backpressure mode for the streaming verbs, mirroring the policy the
buffered verbs already have. Introduce a streaming policy (extend `OutputBufferPolicy` to cover
streaming, or add a dedicated `Command.StreamBuffer(policy)`) threaded from `CommandConfig` into
`StartStdoutStreaming` / the `eventPump`, backing the streaming verbs with
`Channel.CreateBounded<_>(BoundedChannelOptions(capacity, FullMode = …))`. Map the full-mode to
library semantics consistent with the existing `OverflowMode`:

- **Backpressure (`BoundedChannelFullMode.Wait`):** the writer awaits `WriteAsync` /
  `WaitToWriteAsync` — when the consumer stalls, we stop draining the OS pipe, the child blocks
  on its stdout write, and memory is capped at `capacity`. This is the *correct* default for a
  trusted producer you want to slow down (log tailing, a pipeline stage).
- **Drop (`DropOldest` / `DropNewest`):** memory capped, lossy; expose a dropped-line signal
  (a `Truncated` flag / dropped count on the stream) so loss is visible, mirroring the buffered
  `OverflowMode`.
- **Error:** surface `ProcessError.OutputTooLarge` on the streaming enumerator when the cap is
  exceeded, mirroring the buffered fail-loud mode.

Because the writer currently does `TryWrite |> ignore`, switching to bounded means the writer
must either **await** (backpressure) or **handle a failed `TryWrite`** (drop/error). Re-check
the `SingleReader`/`SingleWriter` channel options if backpressure introduces awaiting writers.

**Product decision to settle first:** keep the default unbounded (opt-in bounded only, zero
behavior change, strengthen docs) **or** change the default to a large bounded channel with
backpressure (safer by default, but an observable behavior change — a child that assumed it
could dump-and-exit now blocks). Recommend **opt-in, default unchanged**, plus a clearer doc
warning; changing the default is a `### Changed` behavior break to weigh with the user.

### Why

Today a bursty or untrusted child that floods stdout while the consumer is slow grows the
channel without bound → OOM. The only mitigations are draining fast enough or pairing a
`Timeout`. A bounded/backpressure option lets the consumer cap memory deterministically and
either **slow the producer** (backpressure — the right behavior for most streaming) or **drop
with a visible truncation signal**. It also brings the streaming verbs to parity with the
buffered verbs, which already honor `OutputBufferPolicy`.

### Acceptance criteria

- [ ] New opt-in streaming policy API (bounded capacity + full-mode); public-API snapshot
      updated intentionally.
- [ ] **Backpressure:** a stalled consumer stops the pipe drain and the child observably blocks
      on its stdout write; peak retained memory stays ≤ capacity (test: producer emits N lines,
      consumer reads slowly, assert bounded memory + throttled producer).
- [ ] **Drop modes:** memory bounded to capacity; dropped-line signal exposed and asserted.
- [ ] **Error mode:** `ProcessError.OutputTooLarge` surfaced on the streaming enumerator at the
      cap.
- [ ] **`WaitForLineAsync`** (which reads the same stdout channel, `RunningProcess.fs:681`)
      still works under the bounded policy.
- [ ] **Default behavior unchanged** (still unbounded) unless the user opts in — or, if the
      default is changed, a `### Changed` entry documents it and a way to restore unbounded.
- [ ] Fantomas clean; `docs/streaming.md` updated (including the deadlock footgun — backpressure
      + a consumer that also awaits the producer finishing; recommend pairing with a `Timeout`);
      `CHANGELOG.md` `### Added`/`### Changed`.

### Risks & notes

Backpressure changes observable child *timing*, so it must be opt-in and clearly documented.
There is a genuine deadlock shape (backpressure + a consumer whose progress depends on the
producer exiting) — call it out in the docs and keep the `Timeout` pairing advice.

---

## 3. ILogger observability integration (enhancements)

### Current state (baseline shipped)

All structured logging lives in one internal module, `src/ProcessKit/Log.fs`; every function
takes `ILogger option` and no-ops when `None`/disabled. Only `Microsoft.Extensions.Logging.Abstractions`
is referenced. Six events exist:

| Event | Function | Level | Site |
|-------|----------|-------|------|
| spawn | `Log.spawn` (`Log.fs:20`) | Debug | `RunningProcess.fs:256` |
| exit | `Log.exit` (`Log.fs:32`) | Debug | `RunningProcess.fs:317/363/398/555` |
| timeout | `Log.timeout` (`Log.fs:44`) | Warning | `Command.fs:60` |
| retry | `Log.retry` (`Log.fs:51`) | Debug | `Runner.fs:181` |
| supervisor restart | `Log.supervisorRestart` (`Log.fs:63`) | Debug | `Supervisor.fs:357` |
| storm pause | `Log.stormPause` (`Log.fs:75`) | Warning | `Supervisor.fs:337` |

Gaps: **no `EventId`s** (message templates only), **no** `LoggerMessage.Define` / source-gen
(plain `LogDebug`/`LogWarning`), **no** `BeginScope`/correlation (only spawn carries `pid`;
exit/timeout/retry carry only `{Program}`, so a run's events can't be tied together across a
concurrent fleet), **no** `System.Diagnostics.Activity`/`ActivitySource`, **no**
`System.Diagnostics.Metrics`. The argv/env-redaction guarantee is structural (log functions
only ever receive `program: string`). Logging is threaded via `CommandConfig.Logger` into
`RunningProcess`, so it already covers streaming, not just capture/run.

### What to do

Incremental, most additive. Do the quick wins first.

**Quick wins (low risk):**
- **(A) Stable `EventId`s.** Assign each event a numeric + name `EventId` in `Log.fs` so
  consumers filter/route by ID. Additive.
- **(B) High-performance logging.** Convert the plain `LogDebug`/`LogWarning` calls to cached
  `LoggerMessage.Define`-backed delegates (allocation-free, no format/boxing when the level is
  disabled). `LoggerMessage.Define` is in Abstractions and is F#-friendly; the `[LoggerMessage]`
  source generator needs partial classes and an extra package (awkward in F#) — prefer explicit
  `Define`.
- **(C) Correlation.** Generate a per-run `RunId` at spawn and carry `RunId` + `Pid` on **every**
  run-scoped event (spawn/exit/timeout/retry), plus optionally open a `BeginScope` around the
  run. Adding the fields directly (not only a scope) is more portable, since not every sink
  captures scopes.

**Larger, optional (feeds item 4):**
- **(D) Distributed tracing.** An `ActivitySource` named `"ProcessKit"` emitting one span per
  run, tagged with program, pid, outcome, duration, exit code — **never argv/env**. Free when no
  listener; `System.Diagnostics.DiagnosticSource` is a light dependency. This is the natural
  .NET realization of the Rust crate's `tracing` heritage.
- **(E) Metrics.** A `System.Diagnostics.Metrics.Meter` named `"ProcessKit"` with counters/
  histograms: runs started/completed, exit-outcome distribution, run-duration histogram,
  retries, supervisor restarts, storm pauses, active-process gauge. OpenTelemetry-compatible,
  free when no listener. **Bound tag cardinality** — tag by program name and outcome, never by
  full argv.

Every new signal must uphold the argv/env redaction invariant.

### Why

The baseline is functional but minimal. EventIds and `LoggerMessage.Define` are cheap,
high-value (routing/filtering; zero-alloc hot path). Correlation is the biggest usability gap —
today you cannot tie an exit line back to its spawn/pid across concurrent runs. Tracing +
metrics make ProcessKit first-class in modern OpenTelemetry stacks and honor the Rust crate's
mapping (`tracing` → `ILogger`; `Activity`/`Meter` is the idiomatic .NET extension of that).

### Acceptance criteria

- [ ] Every event has a stable, documented `EventId` (name + number); a test asserts them.
- [ ] Hot path is allocation-free when the level is disabled (`LoggerMessage.Define` delegates);
      a test/benchmark shows no format/alloc when `IsEnabled` is false.
- [ ] Correlation: spawn/exit/retry for one run share the same `RunId` (test asserts it);
      `Pid` present on run-scoped events.
- [ ] argv/env still never logged — extend the existing redaction guarantee test to the new
      signals (fields, trace tags, metric tags).
- [ ] *(If tracing)* `ActivitySource "ProcessKit"`; with a listener a run yields one `Activity`
      with documented tags and no argv/env; free (no side effect/alloc) when no listener.
- [ ] *(If metrics)* `Meter "ProcessKit"` with documented instruments; a `MeterListener` test
      observes expected measurements; tag cardinality bounded (no per-arg tags).
- [ ] Existing `Command.Logger` surface unchanged; new surfaces additive; compiles on both
      `net8.0` and `net10.0` (`Activity`/`Meter` exist on both).
- [ ] Docs (a new observability guide, or extend `commands.md`/`testing.md`); `CHANGELOG.md`
      `### Added`.

### Risks & notes

Keep tracing/metrics in **core** (the BCL `Activity`/`Meter` types need no heavy dependency;
`DiagnosticSource` is light) rather than a new package — decide with the user. The redaction
invariant is the thing most likely to be violated by a careless tag; audit every new tag.

---

## 4. DI Extensions package (enhancements)

### Current state (baseline shipped)

`src/ProcessKit.Extensions.DependencyInjection/` is a **single-file** package
(`ServiceCollectionExtensions.fs`), PackageId `ProcessKit.Extensions.DependencyInjection`,
multi-targeting `net8.0;net10.0`, referencing only the DI + Logging **Abstractions**. It
exposes exactly one member: `ServiceCollectionExtensions.AddProcessKit(IServiceCollection)`,
which `TryAddSingleton<IProcessRunner>` a `JobRunner`, wrapping it in an internal `LoggingRunner`
decorator when the container has an `ILoggerFactory` (category `"ProcessKit"`). `TryAdd` means a
pre-existing `IProcessRunner` registration wins. No options overload, no `IConfiguration`
binding, no keyed/named runners, no `CliClient`/`ProcessGroup`/`Supervisor` registration, no
keyed-services usage.

### What to do

Additive overloads that bring standard .NET DI ergonomics:

- **(A) Options overload.** `AddProcessKit(Action<ProcessKitOptions>)` and/or `IConfiguration`
  binding to configure default timeout, working dir, encoding, ok-codes, retry, and output-buffer
  policy, applied to a template `Command`/`CliClient` via the existing `CliClient.WithDefaults`
  machinery; resolve through `IOptions<ProcessKitOptions>`.
- **(B) Named/keyed external-tool clients.** `AddProcessKitClient(name, program, configure)`
  registering a **keyed** (`.NET 8+ AddKeyedSingleton`, currently unused) `CliClient` /
  `IProcessRunner` per tool, so an app injects "the git client" / "the ffmpeg client" with shared
  defaults by role.
- **(C) Container-managed shared `ProcessGroup`.** Optionally register a shared `ProcessGroup` as
  an `IProcessRunner` whose disposal is tied to the container lifetime (`IAsyncDisposable`), so a
  hosted service can run a whole fleet in one kill-on-dispose group.
- **(D) Supervised hosted child.** Optionally an `IHostedService` wrapper that keeps a `Supervisor`
  command alive for the app's lifetime. This needs `Microsoft.Extensions.Hosting.Abstractions` —
  put it in a **separate** `ProcessKit.Extensions.Hosting` package to keep the DI package
  hosting-free.
- **(E) Observability wiring.** When item 3 lands, register/wire the `ActivitySource`/`Meter` and
  ensure the `LoggingRunner` path picks up the new EventIds.

Preserve `TryAdd` no-clobber semantics and the Abstractions-only footprint where possible.

### Why

Today DI gives you only a bare singleton `IProcessRunner`. Real apps want configured defaults
without hand-building `Command`s, multiple named external-tool clients injected by role, a
container-managed shared process group with correct disposal, and optionally a supervised
long-running child as a hosted service — the standard ergonomics the one-liner doesn't cover.

### Acceptance criteria

- [ ] `AddProcessKit(configure)` binds options; the resolved runner/default `Command` template
      reflects them (test: a configured default timeout is applied to a run).
- [ ] Keyed/named client registration resolves distinct `CliClient`s (test: two named clients
      target different programs; both stay hermetic through an injected test runner).
- [ ] `TryAdd` no-clobber preserved (existing `DependencyInjectionTests.fs` stays green; a
      pre-registered `IProcessRunner` still wins).
- [ ] If a shared `ProcessGroup` is registered, disposing the provider releases it and reaps its
      children (test).
- [ ] New overloads additive; existing `AddProcessKit()` unchanged; the package's public-API
      snapshot (`PublicApi.ProcessKit.Extensions.DependencyInjection.approved.txt`) updated
      intentionally.
- [ ] Compiles on `net8.0`/`net10.0` (keyed services need Abstractions ≥ 8 — already 10.0.9).
- [ ] Fantomas clean; docs (a DI section/guide); `CHANGELOG.md` `### Added`.

### Risks & notes

Keep the DI package hosting-free — hosting helpers (item D) belong in a separate
`ProcessKit.Extensions.Hosting` package; confirm the split with the user. Options binding must
route through the same `CliClient.WithDefaults` path the manual API uses so DI and hand-wired
configs cannot diverge.

---

## 5. Record/replay enhancements

### Current state (baseline shipped)

`RecordReplayRunner` lives in `src/ProcessKit/Cassette.fs` (namespace `ProcessKit.Testing`).
Cassettes are a versioned envelope `CassetteFile { Version; Entries }` (`Cassette.fs:57-64`),
`currentFormatVersion = 1` (`:99`); load rejects any version **≠ 1** (exact inequality,
`:282-286` — note the field/type comments say "major differs" but the code is stricter). Per
entry (`CassetteEntry`, `:18-52`): Program, Args, Cwd, StdinDigest (SHA-256), HasStdin, EnvNames
(names only — values redacted), Stdout, Stderr, Code, TimedOut, Signal, Truncated, DurationMs.
Match key (`:68`) = program + args + cwd + has-stdin + stdin digest; duplicates for a key replay
in order then repeat the last. Verbs: **`CaptureStringAsync` supported** (`:364-365`);
**`CaptureBytesAsync` rejected** with `Unsupported` (text can't reproduce exact bytes, `:367-377`);
**`SpawnAsync`/streaming rejected** with `Unsupported` (`:379-382`). Stdin digest (`:189-204`):
`Bytes` → content hash; `File` → hashes the **path string, not the contents** (`:197`);
`Lines`/`Reader`/`AsyncLines` → `Unsupported` (one-shot, can't key without consuming). Writes are
atomic (temp + `File.Move`) and **0600** on Unix from creation (`:139-182`).

### What to do

Close the two hard rejections first (highest value), then the ergonomics.

- **(A) Bytes-capture support.** Add a binary-safe encoding (store stdout/stderr as base64 when a
  `byte[]` capture is recorded — or always) so `CaptureBytesAsync` replays exact bytes including
  non-UTF-8. Requires a **schema bump to Version 2** with **back-compat load of v1** text entries.
  This removes the single biggest documented gap.
- **(B) Streaming replay.** Support `SpawnAsync`/`StartAsync` on replay by reconstructing a
  `FakeProcess` from the cassette (ordered stdout lines, exit, duration) — exactly what
  `ScriptedRunner` already does for the `StartAsync` verb — so streaming/readiness consumers can
  be exercised hermetically.
- **(C) File-stdin content hashing.** Optionally hash `FromFile` **contents** (not just the path)
  so a cassette matches on what was actually fed; behind an opt-in (reading the file has cost and
  requires the file to exist). Keep the path-only mode available.
- **(D) Matcher & redaction hooks.** Let a caller supply a custom match predicate / arg
  normalizer (e.g. ignore a volatile temp-dir arg) and a redaction hook to scrub secrets from
  stored stdout/stderr (the schema already warns stdout "may contain secrets").
- **(E) Record-on-miss mode.** A VCR-style "append a new entry on miss" mode alongside the current
  strict "miss = `ProcessError.CassetteMiss`" mode, so cassettes are easy to grow. Optional
  pretty-print/inspection helper.
- **(F) Version policy.** Replace the exact-`≠` load check with a documented back-compat policy
  (load compatible older versions, reject truly-incompatible newer ones) so v1 cassettes keep
  loading after the v2 bytes bump; resolve the doc/code comment mismatch at `:60`/`:98`/`:282`.

### Why

The two rejections limit which code can be tested hermetically: a consumer using
`OutputBytesAsync` or the streaming verbs cannot use record/replay at all today. Base64 storage
and `FakeProcess`-backed replay close both. File-content hashing removes a correctness footgun (a
cassette currently matches even if the file's contents changed). Matcher/redaction hooks make
cassettes robust to volatile args and safe to commit. Record-on-miss is the standard ergonomic
that makes cassettes easy to build up.

### Acceptance criteria

- [ ] **Bytes:** recording + replaying `CaptureBytesAsync` reproduces exact bytes including
      invalid-UTF-8 output (test with binary data); v2 schema; **v1 cassettes still load**
      (back-compat test); `Truncated`/`Duration` stay faithful.
- [ ] **Streaming:** `SpawnAsync`/`StartAsync` replays a `FakeProcess` whose `StdoutLinesAsync` /
      readiness probes / exit match the recording (test).
- [ ] **File-stdin content hashing** (if added) is opt-in; a changed file content → cassette miss
      (test); path-only mode still available.
- [ ] **Matcher/redaction hooks:** a custom matcher ignores a volatile arg and still matches
      (test); a redaction hook scrubs a secret from stored stdout (test).
- [ ] **Record-on-miss:** appends a new entry and the next replay hits it (test); **strict mode
      still errors** on miss (existing behavior/test preserved).
- [ ] **Version policy** implemented + documented (load-compatible older, reject incompatible),
      replacing the exact-`≠` check; the doc/code comment mismatch resolved.
- [ ] **Security invariants preserved:** env values still redacted (names only), atomic write +
      0600 preserved for the v2 format, the low-entropy-stdin brute-force caveat still documented.
- [ ] `ProcessKit.Testing` public-API snapshot updated intentionally; Fantomas clean;
      `docs/testing.md` updated; `CHANGELOG.md` `### Added`/`### Changed`.

### Risks & notes

The v1→v2 schema bump is the delicate part — get the back-compat load right and covered by a
test before anything else in this item. Base64 roughly doubles stdout size on disk for binary
captures; document it. Keep all security invariants (redaction, permissions, atomic write) intact
across the new format.

---

## Definition of done (every item)

A stage is complete only when **all** hold, then **stop and wait for the user to confirm** before
starting the next:

1. Build green (warnings-as-errors) on the CI matrix and via `scripts/test-linux.ps1`.
2. Tests green on Windows, Linux, macOS — including any new tests the item's acceptance criteria
   require.
3. Fantomas clean (`dotnet fantomas --check src tests`).
4. Public-API snapshot reconciled (unchanged for internal item 1; intentionally updated for
   additive items 2–5).
5. `CHANGELOG.md` entry under `## [Unreleased]` in the correct subsection.
6. Consumer docs updated where the change is user-visible.
7. Secret-safety invariant re-verified (no argv/env values in any log, trace tag, metric tag, or
   cassette).
