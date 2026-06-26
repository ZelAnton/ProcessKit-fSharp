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
- Tree control on **`ProcessGroup`** (`Signal` / `Suspend` / `Resume` / `Members` / `TerminateAll` /
  `Shutdown`), whole-tree **resource limits**, and **stats** sampling.
- The **`IProcessRunner`** seam with subprocess-free doubles (`ScriptedRunner`,
  `RecordReplayRunner`), the **`CliClient`** wrapper, and the top-level **`Exec`** helpers.
- Optional **`Microsoft.Extensions.Logging`** lifecycle events (`Command.WithLogger`) and the
  **`ProcessKit.Extensions.DependencyInjection`** package (`AddProcessKit`).

The [documentation guide set](docs/README.md) covers all of it in depth.

## Tracked limitations

Deliberate, documented constraints — not correctness bugs — kept here for future hardening:

- **Blocking waits.** Each running process is awaited on a thread-pool thread (and on Windows the
  anonymous pipes are not overlapped, so reads are sync-over-async too). Under heavy concurrency (a
  large `WaitAll`, a `Supervisor`, `Exec.outputAll`) this pressures the thread pool. The public API
  would not change when the internals move to overlapped/registered waits.
- **One terminal consumption per `RunningProcess`.** `WaitForLine` → `StdoutLines` → `Finish`
  compose; `OutputString` / `OutputBytes` / `Wait` are standalone. Mixing terminal verbs (e.g.
  `OutputString` *and* `StdoutLines`) double-pumps the same pipe — documented, not yet guarded.
- **Streaming backlog.** A streamed (`StdoutLines` / `OutputEvents`) consumer that stops draining
  while the child floods grows the channel unbounded; the `OutputBufferPolicy` ceiling applies to
  the *buffered* verbs, and streaming is consumer-paced (pair it with a `Timeout`).
- **Unbounded in-flight line.** The `OutputBufferPolicy` line/byte caps bound the *retained complete
  lines*; a single not-yet-terminated line (a newline-free flood) still grows until EOF. Pair an
  untrusted child with a `Timeout`. (A future hardening caps the in-flight assembly buffer.)
- **Default UTF-8 decoding.** Captured text is decoded UTF-8 by default; a Windows console program
  emitting a legacy OEM code page needs an explicit `StdoutEncoding` / `StderrEncoding`.
- **POSIX pgid-reuse window.** The process-group teardown has a small pid-reuse window on Unix; the
  cgroup v2 backend (engaged when resource limits are requested) does not.

## Future directions

- **Async-I/O hardening.** Move the per-process blocking wait to overlapped/registered waits so
  heavy concurrency no longer pressures the thread pool — an internal change, no public-API impact.
- **In-flight assembly buffer cap.** Cap the not-yet-terminated line buffer so a newline-free flood
  can't outgrow `OutputBufferPolicy`.

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
