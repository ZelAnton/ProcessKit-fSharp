# ProcessKit — roadmap

ProcessKit is feature-complete and its public API is stable. This page records what the library
covers today, the limitations tracked for future hardening, and directions under consideration.
Shipped changes are in [`CHANGELOG.md`](CHANGELOG.md).

## What the library covers

- **Whole-tree kill-on-dispose containment** — a Windows Job Object, a Linux cgroup v2, or a POSIX
  process group — with the active `Mechanism` reported honestly, never a silent downgrade.
- The immutable **`Command`** builder and the full verb set (`Run` / `RunUnit` / `OutputString` /
  `OutputBytes` / `ExitCode` / `Probe` / `Parse` / `TryParse` / `FirstLine` / `Start`), with honest
  results (a non-zero exit is data until you ask for success) and a typed `ProcessError`.
- A live **`RunningProcess`**: line streaming (`StdoutLines` / `OutputEvents`), interactive stdin,
  readiness probes (`WaitForLine` / `WaitForPort` / `WaitFor`), racing (`WaitAny` / `WaitAll`),
  per-run profiling, and stopping — `Kill` (immediate) or `StopAsync` (graceful SIGTERM → grace → SIGKILL).
- Shell-free **`Pipeline`s** with pipefail semantics.
- **`Supervisor`** — restart policies, exponential backoff + jitter, and a failure-storm guard.
- Tree control on **`ProcessGroup`** (`Signal` / `Suspend` / `Resume` / `Members` / `KillAll` /
  `Shutdown`), whole-tree **resource limits**, and **stats** sampling.
- The **`IProcessRunner`** seam with subprocess-free doubles (`ScriptedRunner`,
  `RecordReplayRunner`), the **`CliClient`** wrapper, and the top-level **`Exec`** helpers.
- Optional observability: **`Microsoft.Extensions.Logging`** lifecycle events (`Command.Logger`, with
  stable `EventId`s and per-run correlation), a **`System.Diagnostics` trace span** per run and
  **metrics** on the `ProcessKit` `ActivitySource` / `Meter` (`ProcessKitDiagnostics`), and the
  **`ProcessKit.Extensions.DependencyInjection`** package (`AddProcessKit` with configured defaults,
  keyed per-tool `CliClient`s via `AddProcessKitClient`, and a shared container-managed `ProcessGroup` via
  `AddProcessKitGroup`). Secret-safe — argv and env values never reach a log, span, or metric.

The [documentation guide set](docs/README.md) covers all of it in depth.

## Tracked limitations

Deliberate, documented constraints — not correctness bugs — kept here for future hardening:

- **Blocking POSIX pipe reads.** Exit waits no longer block a dedicated thread on either platform —
  Windows uses a thread-pool registered wait, and POSIX uses an event-driven `SIGCHLD` registration
  (see [`CHANGELOG.md`](CHANGELOG.md)) — and Windows pipe reads are now genuinely overlapped
  (`PipeOptions.Asynchronous`, completed via IOCP). The parent-side **pipe reads on POSIX** are still
  sync-over-async, though — a plain `FileStream` without `FileOptions.Asynchronous` — so they park a
  thread-pool thread per piped stream on Linux/macOS. Under heavy concurrency (a large `WaitAll`, a
  `Supervisor`, `Exec.outputAll`) with many piped POSIX children this still pressures the thread pool.
  The public API would not change when the remaining POSIX reads move to genuinely async I/O.
- **Streaming backlog.** By default a streamed (`StdoutLines` / `OutputEvents`) consumer that stops
  draining while the child floods still grows the channel unbounded. Opt in to
  `Command.StreamBuffer`/`StreamBufferPolicy` to cap it instead — `Backpressure` (the child itself
  slows down), `DropOldest`/`DropNewest` (lossy but bounded, surfaced on
  `RunningProcess.DroppedStreamLineCount`), or `Error` (`ProcessError.OutputTooLarge`); see
  [Streaming](docs/streaming.md#bounding-the-streaming-backlog). The `OutputBufferPolicy` ceiling still
  applies only to the *buffered* verbs, and an unbounded stream is still consumer-paced (pair it with
  a `Timeout`).
- **In-flight line without a byte cap.** With `OutputBufferPolicy.MaxBytes` set, the in-flight
  (not-yet-terminated) line is bounded too — it is force-flushed at the cap, so a newline-free flood
  can't outgrow the buffer. Without a byte cap, or for the consumer-paced *streaming* verbs, a single
  not-yet-terminated line still grows until EOF; bound it with `MaxBytes`, or pair an untrusted child
  with a `Timeout`.
- **Default UTF-8 decoding.** Captured text is decoded UTF-8 by default; a Windows console program
  emitting a legacy OEM code page needs an explicit `StdoutEncoding` / `StderrEncoding`.
- **POSIX pgid-reuse window.** The process-group teardown has a small pid-reuse window on Unix; the
  cgroup v2 backend (engaged when resource limits are requested) does not.

## Future directions

- **POSIX async pipe reads.** Exit waits on both platforms, and Windows pipe reads, are already
  event-driven/overlapped (see [`CHANGELOG.md`](CHANGELOG.md)). The remaining piece is the parent-side
  pipe reads on POSIX, still a plain `FileStream` without `FileOptions.Asynchronous`, so a piped
  Linux/macOS child still parks a thread-pool thread for the life of its stream. An internal change,
  no public-API impact; best driven by a concurrency benchmark.
- **Fully pidfd-driven POSIX exit wait.** The current POSIX exit wait is event-driven via a shared
  `SIGCHLD` registration (not a thread per child). A future revision could open a `pidfd_open`
  handle (Linux >= 5.3) per child and drive the wait off it directly (e.g. `waitid(P_PIDFD)` /
  `epoll`) for O(1) per-child dispatch and pid-reuse-safe reaping, instead of a `SIGCHLD`-triggered
  rescan of every outstanding wait, closer to how tokio waits on Linux — a scalability refinement,
  not a correctness one; the current design already meets the "no thread per child" goal.

## Not currently supported

- **Pseudo-terminal (PTY).** ProcessKit wires pipes, not a tty, so a tool that *demands* a tty (an
  interactive `ssh` / `sudo` password prompt, some credential helpers) won't get one. Drive such
  tools non-interactively, or feed a known answer over interactive stdin.
- **Privilege drop / session detach (`uid` / `gid` / `setsid`).** Not exposed today; `CreateNoWindow`,
  `EnvClear`, and `KeepStdinOpen` cover the common spawn-flag needs.

## Versioning & stability

The public API follows [Semantic Versioning](https://semver.org/): breaking changes land only in a
new major version, so upgrades within a major line are backward-compatible. A public-API snapshot
test guards the surface against accidental change, and every user-visible change ships a
[`CHANGELOG.md`](CHANGELOG.md) entry.
