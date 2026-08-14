# Lifecycle state machine

This page is the authoritative contributor reference for `RunningProcess` output ownership,
exit observation, and teardown. The states below are conceptual names for the public modes. In
the implementation, `Consumption` has `Fresh`, `Buffered`, `StdoutStreaming`,
`StdoutChunkStreaming`, `EventStreaming`, and `Interactive`; the three session states below are
mutually exclusive specializations of `Interactive`, and closing is tracked by the host and
teardown guards rather than by another `Consumption` case.

For the consumer-facing contracts, see [Streaming](../streaming.md). This page concentrates on
the transitions and ownership rules that code extending `RunningProcess` must preserve.

## State transitions

```text
Fresh -> Buffered
  via OutputStringAsync, OutputBytesAsync, WaitAsync, ProfileAsync, or ExitTask first
Fresh -> StdoutStreaming
  via StdoutLinesAsync or StdoutJsonLinesAsync
Fresh -> ChunkStreaming
  via StdoutChunksAsync
Fresh -> EventStreaming
  via OutputEventsAsync
Fresh -> ContentLengthSession
  via the ContentLengthSession constructor
Fresh -> JsonRpcSession
  via the JsonRpcSession constructor
Fresh -> PtySession
  via the PtySession constructor

Buffered -> Closing/Disposed
  via its terminal verb, StopAsync, or disposal
StdoutStreaming -> Closing/Disposed
  via FinishAsync, StopAsync, or disposal
ChunkStreaming -> Closing/Disposed
  via FinishAsync, StopAsync, or disposal
EventStreaming -> Closing/Disposed
  via StopAsync or disposal
ContentLengthSession -> Closing/Disposed
  via StopAsync or disposal
JsonRpcSession -> Closing/Disposed
  via StopAsync or disposal
PtySession -> Closing/Disposed
  via StopAsync or disposal
```

The `OutputEventsAsync`, Content-Length, JSON-RPC, and PTY paths do not support
`FinishAsync`. Their combined outcome is reusable by `ExitTask`, but the caller must still use
`StopAsync` or dispose the owning handle/group to release resources. A line or chunk stream uses
`FinishAsync` as its normal terminal hand-off.

The transition labels have these exact meanings:

- **Fresh** — the handle has been constructed and no output consumer owns its pipes. A readiness
  probe may temporarily drain a snapshot of the pipes and may start the memoized exit wait without
  changing this consumption state.
- **Buffered** — one one-shot verb owns both pipes. Text capture uses `LineBuffer`, byte capture uses
  raw capture for stdout plus a line buffer for stderr, and `WaitAsync` drains without retaining
  output. `ProfileAsync` also drains without retaining output while sampling the process.
- **StdoutStreaming** — one pump frames stdout into the line channel while a second pump captures
  stderr. `StdoutJsonLinesAsync` is a wrapper over this same channel and claim.
- **ChunkStreaming** — the implementation's `StdoutChunkStreaming` state. One pump writes raw,
  non-empty OS reads to the byte channel while stderr is captured separately.
- **EventStreaming** — stdout and stderr have independent line pumps writing tagged events to one
  channel.
- **ContentLengthSession** — the `Interactive` claim is established by constructing
  `ContentLengthSession`. Its parser owns raw stdout frames and a separate pump drains stderr.
- **JsonRpcSession** — constructing it creates exactly one `ContentLengthSession` and immediately
  claims that transport's `FramesAsync()` reader for its router. It is not a second
  `RunningProcess` claim.
- **PtySession** — the `Interactive` claim is established by constructing `PtySession`. Raw decoded
  text is appended to an `ExpectWindow`; no line channel is involved.
- **Closing/Disposed** — backpressure writers are released, teardown is marked, pipe streams are
  closed, and the ownership-specific release runs. `Consumption` is not reset to `Fresh`. There is
  no separately exposed `ReapAfterStop` verb in the .NET API; `StopAsync`, a reaping terminal verb,
  or disposal performs the corresponding wait/release work.

`Kill()` and `Signal(...)` are non-consuming controls and therefore do not themselves move a fresh
handle into an output-consumption state. They affect the child; an existing or later terminal path
still owns output draining, exit observation, and release.

## Ownership and teardown matrix

| State | Consumption claim | Exit task | Pump/channel owner | Reuse and terminal operation |
|---|---|---|---|---|
| Fresh | None | Not started, except that a readiness probe may have memoized the buffered wait | None; a readiness probe can temporarily drain while the state remains fresh | Exactly one consumer may win. `ExitTask` first atomically changes the state to `Buffered` and drains output. |
| Buffered | Permanent `Buffered` claim | `ensureBufferedWait()` creates one `bufferedOutcome` | The winning capture/drain verb owns both pipes | `OutputStringAsync`, `OutputBytesAsync`, `WaitAsync`, and `ProfileAsync` are alternatives and each is once-only. A later consuming verb is refused. |
| StdoutStreaming | Permanent `StdoutStreaming` claim | `streamOutcome` combines exit with both pumps | stdout line pump → single-reader channel; stderr pump → `LineBuffer` | The line/NDJSON enumerator is handed out once. `FinishAsync` rejoins this outcome, returns stderr plus `Outcome`, and reaps. `WaitForLineAsync` may join the same line session. |
| ChunkStreaming | Permanent `StdoutChunkStreaming` claim | `chunkOutcome` combines exit with both pumps | stdout byte pump → single-reader channel; stderr pump → `LineBuffer` | The chunk enumerator is handed out once. `FinishAsync` rejoins this outcome, returns stderr plus `Outcome`, and reaps. |
| EventStreaming | Permanent `EventStreaming` claim | `eventOutcome` combines exit with both pumps | stdout and stderr line pumps → one tagged event channel | `OutputEventsAsync` is handed out once. `FinishAsync` is not a companion; use `StopAsync`, `ExitTask` through the wait helpers followed by disposal, or disposal. |
| ContentLengthSession | Permanent `Interactive` claim | `interactiveOutcome` combines exit, frame parser, and stderr drain | Content-Length parser → frame channel; raw stderr drain | One session per handle; `FramesAsync()` is handed out once. Dispose the run/group to reap after the conversation. |
| JsonRpcSession | The underlying Content-Length session owns the one `Interactive` claim | Same `interactiveOutcome` | JSON-RPC router owns the transport's sole frame reader | One framed transport and one router. Raw frame enumeration and a second session are unavailable. |
| PtySession | Permanent `Interactive` claim | `interactiveOutcome`, exposed by `PtySession.WaitForExitAsync()` | raw stdout/stderr readers → one locked `ExpectWindow` | One session per handle. Waiting observes but does not reap; dispose the run/group afterward. |
| Closing/Disposed | Existing claim remains recorded; no new claim is accepted | An existing outcome may settle during teardown | Channels complete or blocked writers are cancelled; pipe ownership returns to the host teardown | Teardown is idempotent. Private and shared ownership differ as described below. |

The state lock makes claim and outcome publication atomic. Two racing consumers cannot both see
`Fresh`, build independent pumps, or start independent waits. The losing call gets the existing
"already consumed" error rather than a partial view of the child's output.

### Exit-wait contract

Once an exit wait is needed, `ensureBufferedWait()` memoizes the single `host.Wait()` result. A
buffered verb and `ExitTask` therefore observe the same `Outcome`; streaming and interactive states
instead make `ExitTask` reuse their already-combined outcome. The public multi-handle observers
`RunningProcess.WaitAnyAsync` and `WaitAllAsync` are safe to race or repeat against the same handle
because they use that memoized task and never introduce another reader.

`RunningProcess.WaitAsync()` is different: it is itself a buffered consuming verb. It may be called
only once and drains/discards both streams. A second `WaitAsync()` does not act as another observer;
it throws the already-consumed error. Use `WaitAnyAsync`/`WaitAllAsync` when shared exit observation
is required.

On normal terminal paths, the owning verb waits for the exit and all pumps before its reap guard
releases streams or containment. Direct disposal is the deliberate short path: a private handle
hard-kills and reaps through its group even if no `RunningProcess` exit task was started, while a
shared handle detaches its I/O and leaves the child lifetime to its `ProcessGroup`. Consequently,
"wait before release" is a terminal-verb invariant, not a claim that shared-handle disposal waits for
the child.

### Disposal ownership

| Origin | `RunningProcess` disposal | Final lifetime owner |
|---|---|---|
| `Command.StartAsync()` or the default runner | Stops the stdin feeder, closes I/O, and disposes the private per-run group; the whole contained tree is killed and reaped | The `RunningProcess` |
| `group.StartAsync(command)` | Stops the feeder and closes/detaches this run's I/O; it does not kill the shared group | The caller-owned `ProcessGroup`, through `ShutdownAsync` or group disposal |
| `Pipeline.StartAsync()` | The `PipelineSession` teardown kills, waits for, drains, and releases the internally owned group containing every stage | The `PipelineSession` |

All native kill, signal, resize, and release calls are gated against host/group teardown so a late
operation cannot target a closed handle or recycled pid. `Kill()`, a timeout, `StopAsync`, and a
pump fault use the host's gated kill machinery. Cancellation of a one-shot completion verb is wired
to `Kill()` by the verb layer; the token passed to `StartAsync` is checked before spawn only and is
not tracked by a returned live handle. `Signal(signal)` is also gated, but remains a non-consuming
delivery operation rather than a terminal wait. On a shared-group handle, disposal detaches rather
than kills. These distinctions are intentional; the operations do not all imply the same public
lifecycle transition even though they share the backend safety gate.

## Invariants and rules

### Output consumption exclusivity

- Exactly one buffered, line-streaming, chunk-streaming, event-streaming, or interactive session
  claims a `RunningProcess`.
- A claim is permanent for the lifetime of the handle. Pump completion or failure does not return the
  state to `Fresh`, so starting another stream afterward is an error.
- Line/NDJSON, chunk, event, Content-Length frame, and PTY consumers each expose only one reader.
- `WaitAsync()` retains no output, but it still claims and drains both pipes. It is therefore an
  exclusive consumer, not a non-consuming observation.
- Readiness probes are the narrow exception: while the handle is `Fresh`, a probe may temporarily
  drain output and start the shared exit wait without claiming the consumption state. Output drained
  by the probe is not replayed to a later consumer.

### Terminal operation reuse

- `FinishAsync()` may rejoin `StdoutStreaming` or `ChunkStreaming` and collect the captured stderr
  plus `Outcome`. It cannot finish event or interactive sessions. Calling it on a fresh handle starts
  the stdout-streaming machinery even if no enumerator was requested, but its intended use is the
  terminal hand-off after line or chunk streaming.
- Reaching `FinishAsync()` with the line-stream enumerator never handed out — a fresh handle, or one
  only `WaitForLineAsync` looked at — latches a retain-nothing stdout sink for the rest of the run:
  the pump keeps framing lines (handlers, tee, counters, and fault classification are unchanged) but
  stops queueing them, because no reader for that channel exists. The same latch closes the claim gate
  behind it, so one cannot be created either: a later `StdoutLinesAsync`/`StdoutJsonLinesAsync` throws
  the already-consumed `InvalidOperationException` and a later `WaitForLineAsync` returns
  `Unsupported`, exactly as after `WaitAsync`/`ProfileAsync` — never an empty stream or a `NotReady`
  standing in for output that was deliberately dropped. `FinishAsync()` itself stays repeatable.
  Framing and queueing are unchanged once the enumerator *was* handed out, and nothing already queued
  is discarded.
- Streaming verbs cannot be chained. A completed or failed stream still owns its original claim.
- Buffered verbs, each public stream enumerator, and each interactive session constructor are
  once-only alternatives.
- `StopAsync` and teardown are idempotent. `ExitTask` is memoized and is reused by the static
  multi-handle wait helpers and interactive waiters; ordinary `WaitAsync` is not repeatable.

### Abandoned streams

Creating `StdoutLinesAsync`, `StdoutChunksAsync`, or `OutputEventsAsync` starts its pumps immediately;
enumeration is not what starts them. If the returned channel is never consumed or an enumerator is
abandoned, the claim remains and the pumps continue. An unbounded channel can continue accumulating
items. A bounded backpressure channel can leave its writer parked when the backlog fills.

`FinishAsync` (for line/chunk streams), `StopAsync`, `ExitTask`, and disposal cancel the appropriate
backpressure writer before waiting, so terminal cleanup cannot deadlock behind an abandoned bounded
consumer. Disposing a private handle kills its tree; disposing a shared handle only detaches, and the
group remains responsible for the live child. Cancelling an async enumerator stops that enumeration;
it does not reset the claim or, by itself, promise to kill the child.

### PTY, pipelines, and framed sessions

**PTY.** The readers use `Pump.readTextUntilDone`, not the line pump. They decode arbitrary chunks
into `ExpectWindow`; line terminators, line handlers, and line counters do not apply. A waiter's
`Matched`, `Waiting`, or `Ended` decision is one locked `ExpectWindow` step, so a final match racing
end-of-output cannot be lost. `PtySession` supplies the expect/send conversation over this state.

**Pipelines.** The pipeline runner spawns raw contained stages into one internal `ProcessGroup`; it
does not expose a separate `RunningProcess` for every stage. Stage N stdout is copied to stage N+1
stdin by an in-process relay. If the downstream stage exits early, the upstream side observes a
failed write or, on POSIX, may receive `SIGPIPE`. The group is one lifetime unit: cancellation,
timeout, stop, or disposal tears down every stage. A streaming pipeline wraps the final stage's
stdout and whole-chain wait in one `RunningProcess`-backed `PipelineSession`; its outcome is still
classified across the complete chain. See [Pipelines](../pipelines.md) for pipefail and relay rules.

**Content-Length, JSON-RPC, and PTY sessions.** A public session wrapper does not acquire a second
claim after construction. Its constructor establishes the underlying `Interactive` claim once.
`JsonRpcSession` creates one `ContentLengthSession` and owns its only frame reader. Peer output ending
or a framing/JSON parse fault becomes the session's terminal error and completes all pending JSON-RPC
requests with that error. Each timed JSON-RPC request arms its deadline before the frame write and
reuses that same deadline while awaiting the answer, so one budget covers the complete round trip.

## Runtime examples

These are lifecycle outlines, not substitutes for the full API examples in the linked guides.

### One-shot capture

```text
Command.StartAsync
  -> Fresh RunningProcess
  -> OutputStringAsync claims Buffered, pumps stdout/stderr, and waits
  -> reap guard releases the private group
  -> DisposeAsync is an idempotent final cleanup
```

### Streaming with cleanup

```text
Command.StartAsync
  -> Fresh RunningProcess
  -> StdoutLinesAsync claims StdoutStreaming and returns its one channel reader
  -> caller drains the stream
  -> FinishAsync joins streamOutcome, returns stderr + Outcome, and reaps
  -> DisposeAsync is an idempotent final cleanup
```

### Shared-group pipeline

```text
Command.Pipe(stage2).Pipe(stage3)
  -> Pipeline.RunAsync creates one internal ProcessGroup
  -> stages spawn into that group and relays connect stdout(N) to stdin(N+1)
  -> the runner waits for every stage and applies pipefail classification
  -> group disposal releases the whole chain
```

For live streaming, `Pipeline.StartAsync` returns one `PipelineSession`; its stop/disposal path owns
the same whole-chain cleanup.

### JSON-RPC interaction

```text
Command.KeepStdinOpen().StartAsync
  -> Fresh RunningProcess
  -> JsonRpcSession(running) constructs and owns one ContentLengthSession
  -> request/response loop (one deadline per complete request round trip)
  -> optionally finish framed input and observe peer completion
  -> dispose RunningProcess; private-group teardown reaps the tree
```

## Related documentation

- [Streaming](../streaming.md) — consumer-facing stream, frame, readiness, and cleanup contracts.
- [Pipelines](../pipelines.md) — multi-stage construction, relay behavior, and pipefail.
- [Timeouts, retries & cancellation](../timeouts-and-cancellation.md) — deadline,
  cancellation, and kill classification.
- [Internal architecture](architecture.md) — module map, pump structure, and containment backend.
