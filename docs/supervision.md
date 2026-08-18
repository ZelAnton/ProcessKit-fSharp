# Supervision

[Previous: Overview](./)

A `Supervisor` answers a different question from [`retry`](timeouts-and-cancellation.md).
Retry replays *one* run until it succeeds and then hands you that single result; a supervisor
**keeps a child alive** — it runs the command, classifies every exit against a restart policy,
waits out an exponential-backoff delay, and runs it again, until some stop condition ends
supervision. It is a minimal, platform-agnostic keeper in the spirit of `runit`/`systemd`,
built entirely on the [`IProcessRunner`](testing.md) seam, so it never touches the OS directly
and is fully testable without spawning a process.

Each *incarnation* is one full captured run of the command, driven through the runner's
`OutputStringAsync` verb. The command's own `Timeout`, `Stdin`, environment, encoding, and
`OkCodes` therefore apply to every incarnation — including the rule that a
[one-shot stdin source](commands.md#one-shot-stdin-sources-feed-one-incarnation)
(`Stdin.FromStream` / `FromLines` / `FromAsyncLines`) feeds a single incarnation. A supervisor
that can restart therefore refuses such a command up front with `ProcessError.Unsupported`
rather than starting a first incarnation whose successor would find the source empty, and an
incarnation that does start takes the source at its own spawn like any other launch — so a
supervised command wants a reusable source such as `Stdin.FromString`. One thing that does **not**
carry over is the command's own `Command.Retry`: supervision runs the bare runner, so a
supervised command is never internally retried per incarnation. Use the supervisor's restart
policy and backoff instead — see [Supervisor versus retry](#supervisor-versus-retry).

The samples below run inside a `task { }` block and use `match!`; from C# the same surface is
`await`-able fluent methods.

- [Building a supervisor](#building-a-supervisor)
- [Policies: what counts as a crash](#policies-what-counts-as-a-crash)
- [Backoff and jitter](#backoff-and-jitter)
- [Failure storms](#failure-storms)
- [Liveness probes](#liveness-probes)
- [Capturing each incarnation](#capturing-each-incarnation)
- [Stopping](#stopping)
- [The outcome](#the-outcome)
- [Live observability](#live-observability)
- [Supervising inside a shared group](#supervising-inside-a-shared-group)
- [Hermetic testing](#hermetic-testing)
- [Errors and cancellation](#errors-and-cancellation)
- [Supervisor versus retry](#supervisor-versus-retry)

## Building a supervisor

There are two equivalent entry points. The module function threads naturally through `|>`, and
the constructor reads the same from F# and C#:

**F#**

```fsharp
let supervisor = Supervisor.create (Command.create "worker") // the module function…
// …or, identically, the constructor: Supervisor(Command.create "worker")
```

**C#**

```csharp
var supervisor = new Supervisor(new Command("worker")); // constructor
```

The builder is fluent and immutable — every method returns a new `Supervisor`, and building one
spawns nothing. Nothing runs until you call a verb (`RunAsync`):

**F#**

```fsharp
task {
    let supervisor =
        (Supervisor.create (Command.create "my-server" |> Command.args [ "--port"; "8080" ]))
            .Restart(RestartPolicy.OnCrash)                  // Always | OnCrash | Never
            .MaxRestarts(5)                                  // default: unlimited
            .Backoff(TimeSpan.FromMilliseconds 200.0, 2.0)   // base delay, multiplier
            .MaxBackoff(TimeSpan.FromSeconds 30.0)           // cap on any single delay
            .Jitter(true)                                    // default: on
            .StormPause(TimeSpan.FromSeconds 15.0)           // crash-loop guard (off by default)

    match! supervisor.RunAsync() with
    | Ok outcome -> printfn $"ended after {outcome.Restarts} restarts: {outcome.Stopped}"
    | Error err -> eprintfn $"{err.Message}"
}
```

**C#**

```csharp
var supervisor = new Supervisor(new Command("my-server").Args(["--port", "8080"]))
    .Restart(RestartPolicy.OnCrash)               // Always | OnCrash | Never
    .MaxRestarts(5)                               // default: unlimited
    .Backoff(TimeSpan.FromMilliseconds(200), 2.0) // base delay, multiplier
    .MaxBackoff(TimeSpan.FromSeconds(30))         // cap on any single delay
    .Jitter(true)                                 // default: on
    .StormPause(TimeSpan.FromSeconds(15));        // crash-loop guard (off by default)

Console.WriteLine(await supervisor.RunAsync() switch
{
    { IsOk: true, ResultValue: var outcome } => $"ended after {outcome.Restarts} restarts: {outcome.Stopped}",
    { IsOk: false, ErrorValue: var err }    => err.Message,
});
```

The defaults, if you set nothing, are: `RestartPolicy.OnCrash`, **unlimited** restarts, backoff
`200ms × 2.0` capped at 30 s, jitter **on**, and the failure-storm guard **off** (its own
defaults — half-life 30 s, threshold 5.0 — apply only once `StormPause` enables it).

## Callback failures

Supervisor callbacks are synchronous decision hooks, and an exception from any of the four
callback APIs is converted to a typed ProcessError.Io terminal result. The raw exception never
escapes RunAsync or a SupervisionSession.Completion task, and normal session teardown still runs;
the error detail names the callback and retains the source context that was available when it ran.

- StopWhen receives the completed ProcessResult. If it throws, the result context is retained and
  supervision ends with Error(ProcessError.Io ...).
- GiveUpWhen receives the ProcessError being classified. If it throws, the classified error context
  is retained and supervision ends with Error(ProcessError.Io ...).
- OnRestart runs before a restart backoff. If it throws, the restart is abandoned and supervision ends
  with Error(ProcessError.Io ...); no later incarnation is launched.
- OnStormPause runs before a configured storm pause. If it throws, the pause is abandoned and
  supervision ends with Error(ProcessError.Io ...); no later incarnation is launched.

Keep all four callbacks quick and non-blocking. A callback fault is terminal for that supervision
instance, not a reason to retry the callback or silently continue with a different decision.

## Policies: what counts as a crash

A **crash** is any run that is not a *success*: `ProcessResult.IsSuccess` is false. That honors
the command's `OkCodes`, so it covers an exit code outside the accepted set (default `{0}`), a
timeout, a signal-kill, and a failure to spawn. A command with `Command.okCodes [ 0; 2 ]` that
exits `2` *is* a success, so `OnCrash` treats it as a clean exit, not a crash.

| `RestartPolicy` | Restarts after… |
|---|---|
| `OnCrash` *(default)* | crashes only; a clean exit ends supervision (`PolicySatisfied`) |
| `Always` | every completed run, clean or not — pair it with `StopWhen` / `MaxRestarts`, or it loops forever |
| `Never` | nothing: one run, reported as-is (`PolicySatisfied`) |

`RestartPolicy` is `[<RequireQualifiedAccess>]`, so write `RestartPolicy.OnCrash` and friends in
full.

## Backoff and jitter

Before each restart the supervisor sleeps for an exponentially growing delay:

```text
delay(n) = min(base × factor^n, MaxBackoff) × jitter
```

where `n` is an **escalation exponent**: it starts at `0` and climbs by one per restart, but
**resets to `0` after a healthy incarnation** — one that stayed up at least as long as `MaxBackoff`
and wasn't a hang killed by its own timeout. So a long-lived service that crashes only occasionally
restarts promptly at the base delay, while a tight crash loop — or a per-incarnation timeout/hang
loop — keeps climbing and self-throttles. (`n` is **not** the lifetime restart count, which is what
`SupervisionOutcome.Restarts` reports.)

`jitter` is drawn uniformly from `[0.5, 1.5)` per restart when enabled. Jitter is **on by
default** so a fleet of supervised workers restarted by one shared incident does not stampede
back in lockstep; call `.Jitter(false)` for deterministic delays. A `factor` below `1.0` (or
non-finite) is treated as `1.0` — a constant delay, never a shrinking one — and a base delay of
zero (or less) means no wait at all.

For a run that keeps crashing without ever clearing the healthy bar, `n` tracks the restart count:

```text
base = 200ms, factor = 2.0, cap = 30s (before jitter):
n=0 → 200ms   n=1 → 400ms   n=2 → 800ms   n=3 → 1.6s   n=4 → 3.2s
n=5 → 6.4s    n=6 → 12.8s   n=7 → 25.6s   n=8+ → 30s (capped)
```

**F#**

```fsharp
let supervisor =
    (Supervisor.create (Command.create "worker"))
        .Backoff(TimeSpan.FromSeconds 1.0, 1.5) // start at 1s, grow ×1.5
        .MaxBackoff(TimeSpan.FromMinutes 2.0)   // never wait longer than 2 minutes
        .Jitter(false)                          // exact, reproducible delays
```

**C#**

```csharp
var supervisor = new Supervisor(new Command("worker"))
    .Backoff(TimeSpan.FromSeconds(1), 1.5) // start at 1s, grow ×1.5
    .MaxBackoff(TimeSpan.FromMinutes(2))   // never wait longer than 2 minutes
    .Jitter(false);                        // exact, reproducible delays
```

## Failure storms

Backoff spaces out *individual* restarts; `MaxRestarts` is a *lifetime* cap. Neither
distinguishes a service that fails once a day from one that is suddenly crash-looping. The
opt-in **failure-storm guard** does. Enable it with `StormPause`; it is off by default.

Each failure adds `1` to a score that **decays by half every `FailureDecay`** (default 30 s):

```text
score := score × 0.5^(Δt / FailureDecay) + 1     (Δt = time since the previous failure)
```

- **Fails rarely** — the score decays back toward `1` between failures and never reaches the
  threshold, so the guard stays out of the way.
- **Crash-looping** — failures arrive faster than the half-life can drain them, the score climbs
  past `FailureThreshold` (default `5.0`), and the supervisor takes **one collective pause** of
  `StormPause` (jittered per `Jitter`, like the backoff), resets the score, and resumes.

**F#**

```fsharp
task {
    let supervisor =
        (Supervisor.create (Command.create "worker"))
            .StormPause(TimeSpan.FromSeconds 15.0)   // master switch — off by default
            .FailureDecay(TimeSpan.FromSeconds 30.0) // score half-life (default 30s)
            .FailureThreshold(5.0)                   // trip point (default 5.0)

    match! supervisor.RunAsync() with
    | Ok outcome -> printfn $"storm pauses taken: {outcome.StormPauses}"
    | Error err -> eprintfn $"{err.Message}"
}
```

**C#**

```csharp
var supervisor = new Supervisor(new Command("worker"))
    .StormPause(TimeSpan.FromSeconds(15))   // master switch — off by default
    .FailureDecay(TimeSpan.FromSeconds(30)) // score half-life (default 30s)
    .FailureThreshold(5.0);                 // trip point (default 5.0)

Console.WriteLine(await supervisor.RunAsync() switch
{
    { IsOk: true, ResultValue: var outcome } => $"storm pauses taken: {outcome.StormPauses}",
    { IsOk: false, ErrorValue: var err }    => err.Message,
});
```

The fine print:

- **Only failures feed the score.** Crashes and spawn/IO errors count; clean exits restarted
  under `RestartPolicy.Always` do not.
- **The pause runs before the per-restart backoff** — they stack — but the `MaxRestarts` budget
  is checked *first*, so a storm pause never extends an exhausted budget.
- `FailureDecay` and `FailureThreshold` have **no effect** unless `StormPause` is set. A zero
  half-life keeps no history (every failure scores exactly `1.0`, so with the default threshold
  the guard never trips); a non-finite threshold never trips.
- Pauses taken are reported in [`SupervisionOutcome.StormPauses`](#the-outcome) (always `0` when
  the guard is off).

## Liveness probes

A `RestartPolicy` only ever reacts to a run that **ended** — a crash, a timeout, a signal. A process
that is *alive but wedged* — still running, maybe still writing logs, but no longer answering
requests — never trips it, and `Command.IdleTimeout` only catches the subset that also goes silent on
stdout. The opt-in **liveness probe** closes that gap the way a systemd watchdog or a Kubernetes
liveness probe does: it periodically asks whether the live child is still healthy and, after enough
consecutive failures, restarts it. It is **off by default**.

Point it at the child's own health surface — an HTTP endpoint it serves, any async check of your own,
or the attributable peak memory of its contained process tree:

**F#**

```fsharp
task {
    let supervisor =
        (Supervisor.create (Command.create "my-server" |> Command.args [ "--port"; "8080" ]))
            .LivenessHttp(Uri "http://localhost:8080/healthz", TimeSpan.FromSeconds 10.0) // poll every 10s
            .LivenessFailures(3)                     // restart after 3 consecutive failures (default 3)
            .LivenessTimeout(TimeSpan.FromSeconds 2.0) // each probe waits at most 2s for a healthy reply
            .LivenessGrace(TimeSpan.FromSeconds 5.0)   // give the wedged child 5s to stop before a hard kill

    match! supervisor.RunAsync() with
    | Ok outcome -> printfn $"ended: {outcome.Stopped}"
    | Error err -> eprintfn $"{err.Message}"
}
```

**C#**

```csharp
var supervisor = new Supervisor(new Command("my-server").Args(["--port", "8080"]))
    .LivenessHttp(new Uri("http://localhost:8080/healthz"), TimeSpan.FromSeconds(10)) // poll every 10s
    .LivenessFailures(3)                       // restart after 3 consecutive failures (default 3)
    .LivenessTimeout(TimeSpan.FromSeconds(2))  // each probe waits at most 2s for a healthy reply
    .LivenessGrace(TimeSpan.FromSeconds(5));   // give the wedged child 5s to stop before a hard kill
```

For anything that is not a plain 2xx HTTP check, use a response predicate or your own async probe:

**F#**

```fsharp
let byStatus =
    (Supervisor.create (Command.create "worker"))
        .LivenessHttp(Uri "http://localhost:9000/ready", (fun resp -> int resp.StatusCode = 204), TimeSpan.FromSeconds 5.0)

let byPredicate =
    (Supervisor.create (Command.create "worker"))
        .LivenessCheck((fun () -> pingWorkerAsync ()), TimeSpan.FromSeconds 5.0) // returns Task<bool>

let byMemory =
    (Supervisor.create (Command.create "worker"))
        .LivenessMemory(512L * 1024L * 1024L, TimeSpan.FromSeconds 10.0) // restart above 512 MiB
```

**C#**

```csharp
var byStatus = new Supervisor(new Command("worker"))
    .LivenessHttp(new Uri("http://localhost:9000/ready"), resp => (int)resp.StatusCode == 204, TimeSpan.FromSeconds(5));

var byPredicate = new Supervisor(new Command("worker"))
    .LivenessCheck(() => PingWorkerAsync(), TimeSpan.FromSeconds(5)); // returns Task<bool>

var byMemory = new Supervisor(new Command("worker"))
    .LivenessMemory(512L * 1024L * 1024L, TimeSpan.FromSeconds(10)); // restart above 512 MiB
```

`LivenessMemory(maxBytes)` uses the current liveness interval; the two-argument overload sets it in
the same call. It samples whole-tree peak resident memory from the run's private Job Object or cgroup,
so descendants count and a value that crosses the threshold remains over it for that incarnation.
This is deliberately a peak contract, not a current-working-set contract: a transient spike is still a
memory-liveness violation after current usage falls, and `LivenessFailures` only delays the restart
after that crossing. Set `maxBytes` above expected startup/transient peaks when those peaks should not
restart the child. ProcessKit never substitutes leader-only or shared-group memory: when the active
backend cannot provide an attributable tree metric, supervision stops the live child and returns a
typed `ProcessError.Unsupported` (including the POSIX process-group fallback).

Every `LivenessHttp` form also accepts a caller-owned `HttpClient` immediately after the URI. Use it
for authentication headers, custom certificate validation, proxies, or a custom transport such as HTTP
over a Unix domain socket:

**F#**

<!-- docsnippet:imports System.Net.Http -->
```fsharp
use healthClient = new HttpClient()
healthClient.DefaultRequestHeaders.Add("Authorization", "Bearer local-health-token")

let supervised =
    (Supervisor.create (Command.create "worker"))
        .LivenessHttp(Uri "https://localhost:9000/health", healthClient, TimeSpan.FromSeconds 5.0)
```

**C#**

<!-- docsnippet:imports System.Net.Http -->
```csharp
using var healthClient = new HttpClient();
healthClient.DefaultRequestHeaders.Add("Authorization", "Bearer local-health-token");

var supervised = new Supervisor(new Command("worker"))
    .LivenessHttp(new Uri("https://localhost:9000/health"), healthClient, TimeSpan.FromSeconds(5));
```

The supervisor reuses the supplied client across every incarnation and probe attempt but never mutates
or disposes it; the caller remains responsible for its lifetime. HTTP liveness URIs must be absolute,
so configuration errors fail when the supervisor is built rather than restarting a healthy child.

How it behaves:

- **When it restarts.** After `LivenessFailures` **consecutive** failed attempts, the supervisor
  gracefully stops the child (a `LivenessGrace` soft-stop window, then a hard kill) and restarts it
  through the **ordinary** restart path — the same `RestartPolicy`, backoff, jitter, `MaxRestarts`
  budget, and storm guard apply. It is not a second, parallel restart mechanism. For HTTP and
  predicate probes, a single healthy attempt resets the run, so a brief blip that recovers does not
  restart the child. Memory uses the monotonic peak described above: healthy samples reset the run
  only before the peak crosses `maxBytes`, and lower current usage afterward cannot make the sample
  healthy again. The first attempt runs one `LivenessInterval` after the child starts, a natural
  startup window.

The soft phase is the supervised command's `Command.StopSignal` (default `Signal.Term`). The same
setting is therefore honored by an explicit supervision-session `StopAsync`, a liveness restart, and
the hosting extension's `StopAsync`; Windows refuses an unrepresentable custom signal at spawn.
- **Each endpoint attempt is bounded.** One HTTP/predicate attempt gives the endpoint/predicate up to `LivenessTimeout` to
  prove healthy (reusing the same poll/deadline core as `RunningProcess.WaitForHttpAsync`); a
  `false` result, a network failure, a raised exception, or a hung probe all count as one failed
  attempt.
- **It probes the external surface only.** The probe hits the endpoint or runs your predicate — it
  never reads the child's stdout/stderr (those stay yours to capture), and a URL/predicate never
  appears in argv, environment, or a log line. It applies to a live child, so it has no effect on a
  capture-only test double.
- **It is distinguishable.** A liveness-forced restart reports `RestartCause.Liveness` on its
  [`OnRestart`](#live-observability) event (an ordinary restart reports `RestartCause.Exit`), and
  emits the `SupervisorLivenessRestart` log event plus the `processkit.supervisor.liveness_restarts`
  metric — see [Observability](observability.md).

`LivenessFailures`, `LivenessTimeout`, and `LivenessGrace` have **no effect** unless a probe
(`LivenessHttp` / `LivenessCheck` / `LivenessMemory`) is set. A non-positive liveness interval is clamped to 1 ms;
`LivenessTimeout` accepts `TimeSpan.Zero` as a fail-fast attempt but rejects negative values;
`LivenessFailures` must be at least `1`; `LivenessGrace` accepts `TimeSpan.Zero` (kill immediately)
but rejects a negative value.

## Capturing each incarnation

A supervised process can be long-lived and chatty, so capturing its *entire* output across many
restarts risks unbounded heap. By default the supervisor therefore keeps a **bounded tail** —
the most recent 1000 lines — of each incarnation, even when the command's own buffer policy is
unbounded. An explicit bounded or fail-loud command policy is respected as-is; only an unbounded
line count is narrowed to the tail (the overflow mode and any byte cap are preserved, so a
fail-loud command stays fail-loud).

Widen or narrow it with `Capture`:

**F#**

```fsharp
let keepEverything =
    (Supervisor.create (Command.create "worker"))
        .Capture(OutputBufferPolicy.Unbounded) // retain all output of every incarnation

let smallerTail =
    (Supervisor.create (Command.create "worker"))
        .Capture(OutputBufferPolicy.Bounded 200) // keep only the last 200 lines per run
```

**C#**

```csharp
var keepEverything = new Supervisor(new Command("worker"))
    .Capture(OutputBufferPolicy.Unbounded); // retain all output of every incarnation

var smallerTail = new Supervisor(new Command("worker"))
    .Capture(OutputBufferPolicy.Bounded(200)); // keep only the last 200 lines per run
```

The captured output is what you read back from `SupervisionOutcome.FinalResult` after
supervision ends. For the full set of buffer policies and overflow modes, see
[commands.md](commands.md).

For a bounded on-disk log across incarnations, keep one caller-owned
[`RotatingFileSink`](streaming.md#rotating-a-long-lived-log) and attach it with `StdoutTee` or
`StderrTee` before building the supervisor. The supervisor's bounded in-memory tail remains
available in `FinalResult`, while the sink rotates the byte-exact stream. Because rotation is
parent-side, dispose the sink only after the supervision session has ended.

## Stopping

After every completed run three gates are checked, in this order:

1. **`StopWhen(predicate)`** — sees the run's `ProcessResult<string>` and, returning `true`,
   ends supervision *regardless of policy or budget* (→ `StopReason.Predicate`). It is checked on
   every exit, clean or not. The classic pairs it with `Always`: "exit 0 is done, anything else
   is a crash to restart."
2. **The policy** — `OnCrash` stops on a clean exit; `Never` stops after its single run
   (→ `StopReason.PolicySatisfied`).
3. **`MaxRestarts(n)`** — at most *n* restarts, i.e. *n + 1* total runs; an exhausted budget
   reports the last result (→ `StopReason.RestartsExhausted`). `MaxRestarts(0)` means exactly one
   run.

**F#**

```fsharp
task {
    let supervisor =
        (Supervisor.create (Command.create "batch-worker"))
            .Restart(RestartPolicy.Always)               // restart on every exit…
            .StopWhen(fun result -> result.Code = Some 0) // …until one exits cleanly
            .MaxRestarts(50)                              // but give up after 50 restarts

    match! supervisor.RunAsync() with
    | Ok outcome when outcome.Stopped = StopReason.Predicate ->
        printfn "worker finished cleanly"
    | Ok outcome -> printfn $"gave up: {outcome.Stopped}"
    | Error err -> eprintfn $"{err.Message}"
}
```

**C#**

```csharp
var supervisor = new Supervisor(new Command("batch-worker"))
    .Restart(RestartPolicy.Always)                   // restart on every exit…
    .StopWhen(result => result.Code is { Value: 0 }) // …until one exits cleanly
    .MaxRestarts(50);                                // but give up after 50 restarts

Console.WriteLine(await supervisor.RunAsync() switch
{
    { IsOk: true, ResultValue: { Stopped.IsPredicate: true } } => "worker finished cleanly",
    { IsOk: true, ResultValue: var outcome }                   => $"gave up: {outcome.Stopped}",
    { IsOk: false, ErrorValue: var err }                      => err.Message,
});
```

`StopWhen` never sees a run that *failed to start* — a spawn error has no `ProcessResult` to
inspect, so it is classified by the policy alone (see
[Errors and cancellation](#errors-and-cancellation)). `StopReason` is
`[<RequireQualifiedAccess>]`; match it by `StopReason.Predicate` / `.PolicySatisfied` /
`.RestartsExhausted` or test it with `outcome.Stopped.IsPredicate` and friends.

## The outcome

`RunAsync()` resolves to a `Task<Result<SupervisionOutcome, ProcessError>>`. On `Ok`, the
`SupervisionOutcome` reports the last run plus the keeper's telemetry:

| Field | Meaning |
|---|---|
| `FinalResult` | the `ProcessResult<string>` of the final run — the one that ended supervision |
| `Restarts` | how many *re*-runs happened (the first run is not a restart, so `2` means three runs) |
| `Stopped` | the `StopReason` — `Predicate`, `PolicySatisfied`, or `RestartsExhausted` |
| `StormPauses` | failure-storm pauses taken (`0` unless `StormPause` is set) |

An `Ok` outcome means supervision *concluded*, **not** that the child succeeded — a budget can be
exhausted on a still-crashing child. Inspect `FinalResult` for the child's own verdict, or turn
it into a success-or-error with `ProcessResult.ensureSuccess`:

**F#**

```fsharp
task {
    match! (Supervisor.create (Command.create "job")).RunAsync() with
    | Ok outcome ->
        printfn $"runs={outcome.Restarts + 1} reason={outcome.Stopped} pauses={outcome.StormPauses}"

        match ProcessResult.ensureSuccess outcome.FinalResult with
        | Ok final -> printfn $"last run ok: {final.Stdout}"
        | Error err -> eprintfn $"last run failed: {err.Message}"
    | Error err -> eprintfn $"{err.Message}"
}
```

**C#**

```csharp
var outcome = await new Supervisor(new Command("job")).RunAsync();
if (outcome is { IsOk: true, ResultValue: var o })
{
    Console.WriteLine($"runs={o.Restarts + 1} reason={o.Stopped} pauses={o.StormPauses}");

    Console.WriteLine((o.FinalResult.EnsureSuccess()) switch
    {
        { IsOk: true, ResultValue: var final } => $"last run ok: {final.Stdout}",
        { IsOk: false, ErrorValue: var err }  => $"last run failed: {err.Message}",
    });
}
else if (outcome is { IsOk: false, ErrorValue: var err })
    Console.Error.WriteLine(err.Message);
```

## Live observability

`SupervisionOutcome` only arrives once supervision *ends* — unusable for a long-lived (potentially
never-ending) supervised service, where you want to know about a restart or a storm pause as it
happens, e.g. to feed a health check or crash-loop alert. `OnRestart` and `OnStormPause` report
those events live:

**F#**

```fsharp
let supervisor =
    (Supervisor.create (Command.create "worker"))
        .OnRestart(fun e -> printfn $"restart #{e.Restart} for {e.Program} after {e.Delay}")
        .OnStormPause(fun e -> printfn $"storm pause #{e.StormPause} for {e.Program}: {e.Delay}")
```

**C#**

```csharp
var supervisor = new Supervisor(new Command("worker"))
    .OnRestart(e => Console.WriteLine($"restart #{e.Restart} for {e.Program} after {e.Delay}"))
    .OnStormPause(e => Console.WriteLine($"storm pause #{e.StormPause} for {e.Program}: {e.Delay}"));
```

Both callbacks are invoked **synchronously**, from the supervision loop itself — the same async
context driving `RunAsync` — right before the corresponding delay is slept out. Keep handlers quick
and non-blocking: a slow handler delays every restart/pause. `OnRestart` fires on every restart (a
crash, a timeout, a retried transient runner error, or a [liveness](#liveness-probes) failure), never
for the initial run; `OnStormPause` fires once per pause, only when `StormPause` is set. The restart
event's `Cause` (`RestartCause.Exit` vs `RestartCause.Liveness`) tells an ordinary restart apart from
one a liveness probe forced, so a health check can alert on a wedged service distinctly from an
ordinary crash. Both callbacks are purely additive — they never change `SupervisionOutcome`'s final
`Restarts`/`StormPauses`/`Stopped` semantics.

### The event stream

The two callbacks *push* two specific transitions into your code. `Events(capacity)` opts in to the
whole lifecycle as a *pull*-based stream instead: a live `SupervisionSession` then hands out an
`IAsyncEnumerable<SupervisionEvent>` from `EventsAsync()`, which you drain concurrently with
`Completion`/`StopAsync`. It is a third additive view — enabling it changes no restart decision, no
delay, and no outcome, and the callbacks and `Status` keep working exactly as before.

Enable it on the builder, not on the session: the session has to be retaining events from its very
first incarnation, which starts as soon as `StartAsync` returns. Without the opt-in a session
allocates no buffer and builds no event at all, so `RunAsync` pays nothing.

**F#**

```fsharp
task {
    let supervisor =
        (Supervisor.create (Command.create "worker"))
            .Restart(RestartPolicy.Always)
            .Events(256) // opt in; keep at most 256 unread events

    let! session = supervisor.StartAsync()
    let e = session.EventsAsync().GetAsyncEnumerator()

    try
        let mutable go = true

        while go do
            match! e.MoveNextAsync() with
            | true ->
                let event = e.Current

                match event.Kind, event.Restart, event.Delay, event.DroppedEvents with
                | SupervisionEventKind.RestartScheduled, Some restart, Some delay, _ ->
                    printfn $"restart {restart} in {delay}"
                | SupervisionEventKind.EventsDropped, _, _, Some lost -> printfn $"fell behind: {lost} events lost"
                | _ -> printfn $"{event.Name}"
            | false -> go <- false // the stream ends when supervision does
    finally
        e.DisposeAsync().AsTask().Wait()
}
```

**C#**

```csharp
var supervisor = new Supervisor(new Command("worker"))
    .Restart(RestartPolicy.Always)
    .Events(256); // opt in; keep at most 256 unread events

var session = await supervisor.StartAsync();

await foreach (var e in session.EventsAsync())
{
    if (e.Kind == SupervisionEventKind.RestartScheduled && e.Restart is { Value: var restart })
        Console.WriteLine($"restart {restart} in {e.Delay?.Value}");
    else if (e.Kind == SupervisionEventKind.EventsDropped && e.DroppedEvents is { Value: var lost })
        Console.WriteLine($"fell behind: {lost} events lost");
    else
        Console.WriteLine(e.Name);
}
```

Read `Kind` first: it says which transition an event is, and therefore which payload properties carry
a value (every other one is `None`). `Name` is the same fact as a stable lowercase identifier —
`incarnation_started`, `restart_scheduled`, … — for a log field or a metric label.

| `Kind` / `Name` | Reported when | Payload |
|---|---|---|
| `IncarnationStarted` / `incarnation_started` | a child was launched | `Attempt`, `Pid` (`None` for a runner with no live handle) |
| `IncarnationFinished` / `incarnation_finished` | an incarnation produced a result | `Attempt`, `Outcome`, `Duration`, `IsSuccess` |
| `IncarnationFailed` / `incarnation_failed` | an incarnation produced no result at all | `Attempt`, `FailureKind` |
| `RestartScheduled` / `restart_scheduled` | before each backoff delay | `Restart`, `Delay`, `Cause` |
| `StormPaused` / `storm_paused` | before each [failure-storm](#failure-storms) pause | `StormPause`, `Delay` |
| `HealthCheckFailed` / `health_check_failed` | a [liveness](#liveness-probes) probe ended the incarnation | `Attempt`, `IsTerminal` |
| `GaveUp` / `gave_up` | `GiveUpWhen` declared a failure permanent | `Attempt` |
| `Stopped` / `stopped` | supervision ended with an outcome (last event) | `Reason` |
| `SupervisionFailed` / `supervision_failed` | supervision ended with an error (last event) | `FailureKind` |
| `EventsDropped` / `events_dropped` | the consumer fell behind (see below) | `DroppedEvents` |

Every event also carries `Program`. `Attempt` is the 1-based incarnation number (so it runs one ahead
of `SupervisionOutcome.Restarts`), and `HealthCheckFailed.IsTerminal` separates "the unhealthy streak
tripped, the ordinary policy decides what happens next" (`false`) from "the probe itself failed and
supervision is ending" (`true`).

**Bounded, and honest about it.** A supervisor must never pace itself against its observer, so the
stream does **not** apply backpressure. The session retains at most `capacity` unread events; a
consumer that keeps up loses nothing, and one that falls behind (or never reads) makes the supervisor
discard the *oldest* unread events to make room for newer ones. Every such gap is reported rather than
silently swallowed: the next event the consumer sees is an `EventsDropped` carrying exactly how many
were lost, immediately before the oldest event that survived, and `session.DroppedEventCount` keeps the
lifetime total (the supervision analogue of
[`RunningProcess.DroppedStreamLineCount`](streaming.md)). `Events()` with no argument uses a default
capacity of 128 — one crash-restart cycle costs three events, so an ordinary consumer never lags.

**One consumer.** Reading the buffer is destructive, so a second consumer would steal events from the
first: `EventsAsync()` hands the stream out once and throws `InvalidOperationException` on a repeat
call (and on a session whose supervisor never called `Events`).

**Non-secret by construction.** Events carry lifecycle facts only — counters, a pid, an `Outcome`,
durations, the program name, and coarse failure/stop classifications. They never carry argv,
environment values, captured stdout/stderr, or a `ProcessError`'s message; a launch failure is reported
as its stable class (`spawn`, `not_found`, `io`, …) rather than as the error itself. That is what makes
it safe to forward the whole stream to a log or metrics sink, and it matches the taxonomy
[`MemberInfo` and the library's own logging](observability.md) already follow.

## Supervising inside a shared group

The supervisor runs every incarnation through an `IProcessRunner` — the default is a private
`JobRunner` (a fresh kill-on-dispose group per incarnation). Override it with `WithRunner`. The
headline production variant injects a [`ProcessGroup`](process-groups.md), which is itself an
`IProcessRunner`, so every incarnation — and everything it spawns — lives in one shared
kill-on-dispose container:

**F#**

```fsharp
task {
    match ProcessGroup.Create() with
    | Error err -> eprintfn $"{err.Message}"
    | Ok group ->
        use group = group // the group outlives supervision; disposing it reaps any strays

        let supervisor =
            (Supervisor.create (Command.create "worker"))
                .WithRunner(group)
                .Restart(RestartPolicy.OnCrash)
                .MaxRestarts(10)

        match! supervisor.RunAsync() with
        | Ok outcome -> printfn $"stopped: {outcome.Stopped}"
        | Error err -> eprintfn $"{err.Message}"
}
```

**C#**

```csharp
var created = ProcessGroup.Create();
if (created is { IsOk: false, ErrorValue: var createErr })
{
    Console.Error.WriteLine(createErr.Message);
    return;
}

using var group = created.GetValueOrThrow(); // the group outlives supervision; disposing it reaps any strays

var supervisor = new Supervisor(new Command("worker"))
    .WithRunner(group)
    .Restart(RestartPolicy.OnCrash)
    .MaxRestarts(10);

Console.WriteLine(await supervisor.RunAsync() switch
{
    { IsOk: true, ResultValue: var outcome } => $"stopped: {outcome.Stopped}",
    { IsOk: false, ErrorValue: var err }    => err.Message,
});
```

The group is yours: it outlives supervision, so dispose it (or `ShutdownAsync` it) to tear down
anything still running once the keeper has stopped. One interaction to mind — do not supervise
into a group you have [suspended](process-groups.md); under the cgroup mechanism a restarted
child would start frozen (and the spawn itself can block). Resume the group first.

## Hermetic testing

The same injection point makes supervision logic testable with **no real process**. Pass a
`ScriptedRunner` (from [`ProcessKit.Testing`](testing.md)) that returns canned replies, and
assert the restart and stop behavior deterministically — pair it with `.Jitter(false)` for
reproducible timing:

For tests that must also control elapsed time, build the command with a deterministic
`TimeProvider` (`Command.TimeProvider(provider)` / `Command.timeProvider provider`). The provider
drives the supervisor's restart backoff, storm-score decay, liveness interval, and liveness
readiness deadline; production continues to use `TimeProvider.System` by default.

**F#**

```fsharp
task {
    // Fail twice, then succeed — under OnCrash this should restart twice and stop clean.
    let mutable calls = 0

    let runner =
        (ScriptedRunner())
            .When((fun _ -> calls <- calls + 1; calls <= 2), Reply.Fail(1, "boom"))
            .Fallback(Reply.Ok "ready")

    let supervisor =
        (Supervisor.create (Command.create "worker"))
            .WithRunner(runner)
            .Restart(RestartPolicy.OnCrash)
            .Jitter(false)

    match! supervisor.RunAsync() with
    | Ok outcome ->
        // Restarts = 2, Stopped = PolicySatisfied (the clean third run ends OnCrash supervision).
        printfn $"restarts={outcome.Restarts} reason={outcome.Stopped}"
    | Error err -> eprintfn $"{err.Message}"
}
```

**C#**

```csharp
// Fail twice, then succeed — under OnCrash this should restart twice and stop clean.
var calls = 0;

var runner = new ScriptedRunner()
    .When(_ => { calls++; return calls <= 2; }, Reply.Fail(1, "boom"))
    .Fallback(Reply.Ok("ready"));

var supervisor = new Supervisor(new Command("worker"))
    .WithRunner(runner)
    .Restart(RestartPolicy.OnCrash)
    .Jitter(false);

Console.WriteLine(await supervisor.RunAsync() switch
{
    // Restarts = 2, Stopped = PolicySatisfied (the clean third run ends OnCrash supervision).
    { IsOk: true, ResultValue: var outcome } => $"restarts={outcome.Restarts} reason={outcome.Stopped}",
    { IsOk: false, ErrorValue: var err }    => err.Message,
});
```

`Reply.Ok` / `Reply.Fail` / `Reply.Exit` / `Reply.Signalled` cover the result shapes a crash
classifier cares about. See [testing.md](testing.md) for the full seam, including scripting by
exact argv (`On`) versus predicate (`When`) and record/replay cassettes.

## Errors and cancellation

A run that produces **no result at all** — a spawn or I/O failure, where there is no
`ProcessResult` to judge — is treated as a crash: the supervisor restarts it (with backoff)
unless the policy is `Never` or the budget is exhausted, in which case that `ProcessError`
surfaces as `RunAsync`'s `Error`. Because such a run never started, `StopWhen` does not see it; only
the policy and the budget apply.

A **cancelled** incarnation is terminal. If the token is already cancelled at the top of an
iteration, or an incarnation resolves to `ProcessError.Cancelled`, `RunAsync` returns that
`Cancelled` immediately — regardless of policy or remaining budget. The token never un-cancels,
so a restart could only produce another instantly-cancelled run; the supervisor refuses the
futile loop. Pass the token to `RunAsync(token)`:

**F#**

```fsharp
task {
    use cts = new CancellationTokenSource()
    let supervised = (Supervisor.create (Command.create "worker")).RunAsync(cts.Token)

    // elsewhere — a shutdown signal, a sibling failure:
    cts.Cancel()

    match! supervised with
    | Error(ProcessError.Cancelled _) -> printfn "supervision cancelled"
    | _ -> ()
}
```

**C#**

```csharp
using var cts = new CancellationTokenSource();
var supervised = new Supervisor(new Command("worker")).RunAsync(cts.Token);

// elsewhere — a shutdown signal, a sibling failure:
cts.Cancel();

if (await supervised is { IsOk: false, ErrorValue: { IsCancelled: true } })
    Console.WriteLine("supervision cancelled");
```

A **graceful stop** of a live supervision session (`Supervisor.StartAsync`, then
`SupervisionSession.StopAsync`) normally ends supervision as `Ok` with `StopReason.Stopped`,
reporting the honest result of a live-handle incarnation. A capture-only incarnation has no process
handle or graceful-stop mechanism, so the session publishes a per-incarnation cancellation lever
and `StopAsync` cancels it immediately without applying the grace period. If cancellation prevents
the runner from reporting an exit status, the final result uses `Outcome.Unobserved`; the session
still completes normally with `StopReason.Stopped`. Cancellation through the token passed to
`StartAsync`, without a `StopAsync` request, remains `ProcessError.Cancelled`.

A stop that lands before any incarnation has produced a result falls under the no-result rule above.
The supervisor will not start a child just to manufacture an outcome: it returns the last failure
from an incarnation that produced no result, or `ProcessError.Cancelled` when there is no such
failure. That `Cancelled` is produced when the token did **not** fire. To distinguish external
cancellation from a deliberate stop, consult the token, not the error shape.

For the full model of captured-versus-raised deadlines and how cancellation differs from a
timeout, see [timeouts-and-cancellation.md](timeouts-and-cancellation.md).

## Supervisor versus retry

The two layers answer different questions, and they compose rather than overlap:

| | `Command.Retry` | `Supervisor` |
|---|---|---|
| Question | "run this once, replaying on failure" | "keep this alive across exits" |
| Scope | a single logical run | an ongoing lifecycle of many runs |
| Stops on | the first success (or attempts exhausted) | a policy / predicate / budget — including after clean exits |
| Spacing | a fixed retry delay | exponential backoff + jitter + a storm guard |
| Reports | the one successful (or last) result | a `SupervisionOutcome` with restart count and reason |

A supervised command's own `Command.Retry` is **not** applied per incarnation — supervision runs
the bare runner — so configure resilience through the supervisor's policy and backoff, not the
command's retry. Reach for retry when you want one value out of a flaky one-shot; reach for a
supervisor when you want a process to stay up. See
[timeouts-and-cancellation.md](timeouts-and-cancellation.md) for retry.

---

Next: [Testing your code](testing.md)
