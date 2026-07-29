# Performance and scalability

[Previous: Overview](./)

ProcessKit is designed to keep the managed cost of many live children proportional to useful work,
not to reserve one blocked thread per process or pipe. The operating system still sets the real
ceiling: process, handle/file-descriptor, pipe-buffer, memory, cgroup, and Job Object limits usually
arrive before a single library-wide concurrency number would be meaningful.

## What waits when a child is idle

Process exit is event-driven on every supported platform:

- Linux 5.4+ registers pidfds with one shared epoll reaper. Older Linux uses the shared SIGCHLD
  fallback.
- macOS registers `EVFILT_PROC` / `NOTE_EXIT` with one shared kqueue reaper.
- Windows uses registered waits over process handles; a pool wait thread multiplexes many handles.

Piped output uses asynchronous stream reads. An idle child therefore does not need a dedicated
managed thread parked in `Read` or `waitpid`. Work resumes when the OS reports exit or pipe data.
This is a scaling property, not a promise that spawning a process is cheap: executable loading,
container setup, antivirus, shell startup, and the child itself can dominate short commands.

## Choose the output contract deliberately

The default capture and streaming policies are unbounded for compatibility. Under load, make the
retention/backpressure decision explicit:

- Use `OutputBufferPolicy.Bounded` for one-shot capture when only a tail or a fail-loud ceiling is
  acceptable.
- Use `Command.StreamBuffer(StreamBufferPolicy.Bounded(...))` when a slow line consumer must not
  create an unlimited channel backlog. `Backpressure` preserves every line but can eventually block
  the child on a full OS pipe; `DropOldest`/`DropNewest` stay bounded but are lossy; `Error` terminates
  the run visibly once the channel is full.
- Use `StdoutToFile` for the lowest-overhead direct file redirect. Use `RotatingFileSink` through a
  tee when bounded rotation matters more than keeping the child's fd independent of the parent pump.
- Prefer byte framing (`ContentLengthSession`) or raw bytes when the protocol is byte-defined. Avoid
  decode/re-encode work and accidental line buffering for LSP/DAP-style transports.

Every unread stdout/stderr pipe is finite. If a live-handle workflow does not consume output, use a
verb that drains it, redirect it, or set it to `Null`; otherwise the child can block regardless of
how efficiently its exit is awaited.

## Scaling a fleet

Start with a representative fan-out and measure on the deployment OS. Increase it while watching:

- process count and OS handle/file-descriptor limits;
- thread-pool active/queued work (it should not rise by one permanently parked worker per idle child);
- retained bytes and dropped streaming lines;
- child startup latency, CPU, and memory;
- `ProcessGroup.Stats()` or per-run profiling when the active containment mechanism supports it.

Use `RunningProcess.WaitAllAsync`/`WaitAnyAsync` to coordinate existing handles without introducing
another waiter per child. For bulk work, bound producer concurrency in the caller rather than
launching an unlimited task array: ProcessKit contains each tree, but it does not guess an application
or machine-specific admission limit.

## Observability cost

No logger is the default. Lifecycle logging uses cached `LoggerMessage` delegates and exits early
when its level is disabled. Metrics and activities use bounded tag sets (program and closed outcome
labels; never argv or environment values), but enabled exporters still allocate, batch, and perform
I/O. Measure with the same logger, meter listener, sampler, and exporter configuration used in
production. High-cardinality program names generated per invocation are a caller choice and can make
an otherwise bounded schema expensive downstream.

## Run and interpret the benchmarks

Build Release first, then run all BenchmarkDotNet scenarios:

```powershell
dotnet build ProcessKit.slnx --configuration Release
dotnet run --no-build --configuration Release --framework net10.0 --project benchmarks/ProcessKit.Benchmarks/ProcessKit.Benchmarks.fsproj
```

Use `-- --filter *Pump*`, `*Concurrency*`, `*SingleSpawnCapture*`, `*StreamingBenchmarks*`, or
`*ConcurrentBatch*` after the project path to narrow a local investigation. The suite covers
line-pump framing and allocations, disabled/no-logger calls, single spawn+capture, large line
streaming, and concurrent batches against raw `System.Diagnostics.Process` and CliWrap.

The weekly/manual [benchmark workflow](../.github/workflows/benchmarks.yml) runs the reduced `--ci`
job and uploads `BenchmarkDotNet.Artifacts` as `benchmark-results`. GitHub-hosted runners are noisy:
do not treat a single mean or a few-percent delta as a regression. Look for repeated changes in
relative shape, allocations, or thread counts, then reproduce locally on stable hardware with the
default statistically rigorous job.

For implementation detail, see [the containment backend contract](internals/architecture.md#containment-backend-contract)
and [the benchmark architecture notes](internals/architecture.md#benchmarking-hot-paths).
