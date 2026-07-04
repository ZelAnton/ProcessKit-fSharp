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
  readiness probes (`WaitForLine` / `WaitForPort` / `WaitFor`), racing (`WaitAny` / `WaitAll`), and
  per-run profiling.
- Shell-free **`Pipeline`s** with pipefail semantics.
- **`Supervisor`** — restart policies, exponential backoff + jitter, and a failure-storm guard.
- Tree control on **`ProcessGroup`** (`Signal` / `Suspend` / `Resume` / `Members` / `KillAll` /
  `Shutdown`), whole-tree **resource limits**, and **stats** sampling.
- The **`IProcessRunner`** seam with subprocess-free doubles (`ScriptedRunner`,
  `RecordReplayRunner`), the **`CliClient`** wrapper, and the top-level **`Exec`** helpers.
- Optional observability: **`Microsoft.Extensions.Logging`** lifecycle events (`Command.Logger`, with
  stable `EventId`s and per-run correlation), a **`System.Diagnostics` trace span** per run and
  **metrics** on the `ProcessKit` `ActivitySource` / `Meter` (`ProcessKitDiagnostics`), and the
  **`ProcessKit.Extensions.DependencyInjection`** package (`AddProcessKit`). Secret-safe — argv and env
  values never reach a log, span, or metric.

The [documentation guide set](docs/README.md) covers all of it in depth.

## Tracked limitations

Deliberate, documented constraints — not correctness bugs — kept here for future hardening:

- **Blocking pipe reads (and the POSIX exit wait).** The Windows exit wait now uses a thread-pool
  registered wait (no dedicated thread per child), but the parent-side **pipe reads** are still
  sync-over-async on both platforms — they park a thread-pool thread per piped stream — and the
  **POSIX exit wait** still blocks a thread per child. Under heavy concurrency (a large `WaitAll`, a
  `Supervisor`, `Exec.outputAll`) this pressures the thread pool. The public API would not change
  when the remaining reads/waits move to overlapped (Windows) / `pidfd`-or-reaper (POSIX) I/O.
- **Streaming backlog.** A streamed (`StdoutLines` / `OutputEvents`) consumer that stops draining
  while the child floods grows the channel unbounded; the `OutputBufferPolicy` ceiling applies to
  the *buffered* verbs, and streaming is consumer-paced (pair it with a `Timeout`).
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

- **Async-I/O hardening.** The Windows exit wait is now a registered wait; the remaining work is
  overlapped (named-pipe) reads on Windows and an async exit wait on POSIX (`pidfd` on Linux ≥5.3, a
  single reaper thread otherwise) so heavy concurrency no longer parks a thread per piped stream /
  per POSIX child. An internal change, no public-API impact; best driven by a concurrency benchmark.

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
