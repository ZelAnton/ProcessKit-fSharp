# Supervision

[‹ docs index](README.md)

A `Supervisor` answers a different question from [`retry`](timeouts-and-cancellation.md).
Retry replays *one* run until it succeeds and then hands you that single result; a supervisor
**keeps a child alive** — it runs the command, classifies every exit against a restart policy,
waits out an exponential-backoff delay, and runs it again, until some stop condition ends
supervision. It is a minimal, platform-agnostic keeper in the spirit of `runit`/`systemd`,
built entirely on the [`IProcessRunner`](testing.md) seam, so it never touches the OS directly
and is fully testable without spawning a process.

Each *incarnation* is one full captured run of the command, driven through the runner's
`OutputString` verb. The command's own `Timeout`, `Stdin`, environment, encoding, and
`OkCodes` therefore apply to every incarnation — with the usual
[one-shot-stdin caveat](commands.md) for the second run onward (feed a reusable source such as
`Stdin.FromString` rather than a stream you can read only once). One thing that does **not**
carry over is the command's own `Command.Retry`: supervision runs the bare runner, so a
supervised command is never internally retried per incarnation. Use the supervisor's restart
policy and backoff instead — see [Supervisor versus retry](#supervisor-versus-retry).

The samples below run inside a `task { }` block and use `match!`; from C# the same surface is
`await`-able fluent methods.

- [Building a supervisor](#building-a-supervisor)
- [Policies: what counts as a crash](#policies-what-counts-as-a-crash)
- [Backoff and jitter](#backoff-and-jitter)
- [Failure storms](#failure-storms)
- [Capturing each incarnation](#capturing-each-incarnation)
- [Stopping](#stopping)
- [The outcome](#the-outcome)
- [Supervising inside a shared group](#supervising-inside-a-shared-group)
- [Hermetic testing](#hermetic-testing)
- [Errors and cancellation](#errors-and-cancellation)
- [Supervisor versus retry](#supervisor-versus-retry)

## Building a supervisor

There are two equivalent entry points. The module function threads naturally through `|>`, and
the constructor reads the same from F# and C#:

```fsharp
open ProcessKit

let supervisor = Supervisor.create (Command.create "worker") // module
let supervisor = Supervisor(Command.create "worker")         // constructor
```

The builder is fluent and immutable — every method returns a new `Supervisor`, and building one
spawns nothing. Nothing runs until you call a verb (`Run`):

```fsharp
open ProcessKit
open System

task {
    let supervisor =
        (Supervisor.create (Command.create "my-server" |> Command.args [ "--port"; "8080" ]))
            .Restart(RestartPolicy.OnCrash)                  // Always | OnCrash | Never
            .MaxRestarts(5)                                  // default: unlimited
            .Backoff(TimeSpan.FromMilliseconds 200.0, 2.0)   // base delay, multiplier
            .MaxBackoff(TimeSpan.FromSeconds 30.0)           // cap on any single delay
            .Jitter(true)                                    // default: on
            .StormPause(TimeSpan.FromSeconds 15.0)           // crash-loop guard (off by default)

    match! supervisor.Run() with
    | Ok outcome -> printfn $"ended after {outcome.Restarts} restarts: {outcome.Stopped}"
    | Error err -> eprintfn $"{err.Message}"
}
```

The defaults, if you set nothing, are: `RestartPolicy.OnCrash`, **unlimited** restarts, backoff
`200ms × 2.0` capped at 30 s, jitter **on**, and the failure-storm guard **off** (its own
defaults — half-life 30 s, threshold 5.0 — apply only once `StormPause` enables it).

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

Before each restart the supervisor sleeps. The *n*-th restart (0-based) waits:

```text
delay(n) = min(base × factor^n, MaxBackoff) × jitter
```

with `jitter` drawn uniformly from `[0.5, 1.5)` per restart when enabled. Jitter is **on by
default** so a fleet of supervised workers restarted by one shared incident does not stampede
back in lockstep; call `.Jitter(false)` for deterministic delays. A `factor` below `1.0` (or
non-finite) is treated as `1.0` — a constant delay, never a shrinking one — and a base delay of
zero (or less) means no wait at all.

```text
base = 200ms, factor = 2.0, cap = 30s (before jitter):
#0 → 200ms   #1 → 400ms   #2 → 800ms   #3 → 1.6s   #4 → 3.2s
#5 → 6.4s    #6 → 12.8s   #7 → 25.6s   #8+ → 30s (capped)
```

```fsharp
open ProcessKit
open System

let supervisor =
    (Supervisor.create (Command.create "worker"))
        .Backoff(TimeSpan.FromSeconds 1.0, 1.5) // start at 1s, grow ×1.5
        .MaxBackoff(TimeSpan.FromMinutes 2.0)   // never wait longer than 2 minutes
        .Jitter(false)                          // exact, reproducible delays
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

```fsharp
open ProcessKit
open System

task {
    let supervisor =
        (Supervisor.create (Command.create "worker"))
            .StormPause(TimeSpan.FromSeconds 15.0)   // master switch — off by default
            .FailureDecay(TimeSpan.FromSeconds 30.0) // score half-life (default 30s)
            .FailureThreshold(5.0)                   // trip point (default 5.0)

    match! supervisor.Run() with
    | Ok outcome -> printfn $"storm pauses taken: {outcome.StormPauses}"
    | Error err -> eprintfn $"{err.Message}"
}
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

## Capturing each incarnation

A supervised process can be long-lived and chatty, so capturing its *entire* output across many
restarts risks unbounded heap. By default the supervisor therefore keeps a **bounded tail** —
the most recent 1000 lines — of each incarnation, even when the command's own buffer policy is
unbounded. An explicit bounded or fail-loud command policy is respected as-is; only an unbounded
line count is narrowed to the tail (the overflow mode and any byte cap are preserved, so a
fail-loud command stays fail-loud).

Widen or narrow it with `Capture`:

```fsharp
open ProcessKit

let keepEverything =
    (Supervisor.create (Command.create "worker"))
        .Capture(OutputBufferPolicy.Unbounded) // retain all output of every incarnation

let smallerTail =
    (Supervisor.create (Command.create "worker"))
        .Capture(OutputBufferPolicy.Bounded 200) // keep only the last 200 lines per run
```

The captured output is what you read back from `SupervisionOutcome.FinalResult` after
supervision ends. For the full set of buffer policies and overflow modes, see
[commands.md](commands.md).

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

```fsharp
open ProcessKit

task {
    let supervisor =
        (Supervisor.create (Command.create "batch-worker"))
            .Restart(RestartPolicy.Always)               // restart on every exit…
            .StopWhen(fun result -> result.Code = Some 0) // …until one exits cleanly
            .MaxRestarts(50)                              // but give up after 50 restarts

    match! supervisor.Run() with
    | Ok outcome when outcome.Stopped = StopReason.Predicate ->
        printfn "worker finished cleanly"
    | Ok outcome -> printfn $"gave up: {outcome.Stopped}"
    | Error err -> eprintfn $"{err.Message}"
}
```

`StopWhen` never sees a run that *failed to start* — a spawn error has no `ProcessResult` to
inspect, so it is classified by the policy alone (see
[Errors and cancellation](#errors-and-cancellation)). `StopReason` is
`[<RequireQualifiedAccess>]`; match it by `StopReason.Predicate` / `.PolicySatisfied` /
`.RestartsExhausted` or test it with `outcome.Stopped.IsPredicate` and friends.

## The outcome

`Run()` resolves to a `Task<Result<SupervisionOutcome, ProcessError>>`. On `Ok`, the
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

```fsharp
open ProcessKit

task {
    match! (Supervisor.create (Command.create "job")).Run() with
    | Ok outcome ->
        printfn $"runs={outcome.Restarts + 1} reason={outcome.Stopped} pauses={outcome.StormPauses}"

        match ProcessResult.ensureSuccess outcome.FinalResult with
        | Ok final -> printfn $"last run ok: {final.Stdout}"
        | Error err -> eprintfn $"last run failed: {err.Message}"
    | Error err -> eprintfn $"{err.Message}"
}
```

## Supervising inside a shared group

The supervisor runs every incarnation through an `IProcessRunner` — the default is a private
`JobRunner` (a fresh kill-on-dispose group per incarnation). Override it with `WithRunner`. The
headline production variant injects a [`ProcessGroup`](process-groups.md), which is itself an
`IProcessRunner`, so every incarnation — and everything it spawns — lives in one shared
kill-on-dispose container:

```fsharp
open ProcessKit
open System

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

        match! supervisor.Run() with
        | Ok outcome -> printfn $"stopped: {outcome.Stopped}"
        | Error err -> eprintfn $"{err.Message}"
}
```

The group is yours: it outlives supervision, so dispose it (or `Shutdown` it) to tear down
anything still running once the keeper has stopped. One interaction to mind — do not supervise
into a group you have [suspended](process-groups.md); under the cgroup mechanism a restarted
child would start frozen (and the spawn itself can block). Resume the group first.

## Hermetic testing

The same injection point makes supervision logic testable with **no real process**. Pass a
`ScriptedRunner` (from [`ProcessKit.Testing`](testing.md)) that returns canned replies, and
assert the restart and stop behavior deterministically — pair it with `.Jitter(false)` for
reproducible timing:

```fsharp
open ProcessKit
open ProcessKit.Testing

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

    match! supervisor.Run() with
    | Ok outcome ->
        // Restarts = 2, Stopped = PolicySatisfied (the clean third run ends OnCrash supervision).
        printfn $"restarts={outcome.Restarts} reason={outcome.Stopped}"
    | Error err -> eprintfn $"{err.Message}"
}
```

`Reply.Ok` / `Reply.Fail` / `Reply.Exit` / `Reply.Signalled` cover the result shapes a crash
classifier cares about. See [testing.md](testing.md) for the full seam, including scripting by
exact argv (`On`) versus predicate (`When`) and record/replay cassettes.

## Errors and cancellation

A run that produces **no result at all** — a spawn or I/O failure, where there is no
`ProcessResult` to judge — is treated as a crash: the supervisor restarts it (with backoff)
unless the policy is `Never` or the budget is exhausted, in which case that `ProcessError`
surfaces as `Run`'s `Error`. Because such a run never started, `StopWhen` does not see it; only
the policy and the budget apply.

A **cancelled** incarnation is terminal. If the token is already cancelled at the top of an
iteration, or an incarnation resolves to `ProcessError.Cancelled`, `Run` returns that
`Cancelled` immediately — regardless of policy or remaining budget. The token never un-cancels,
so a restart could only produce another instantly-cancelled run; the supervisor refuses the
futile loop. Pass the token to `Run(token)`:

```fsharp
open ProcessKit
open System.Threading

task {
    use cts = new CancellationTokenSource()
    let supervised = (Supervisor.create (Command.create "worker")).Run(cts.Token)

    // elsewhere — a shutdown signal, a sibling failure:
    cts.Cancel()

    match! supervised with
    | Error(ProcessError.Cancelled _) -> printfn "supervision cancelled"
    | _ -> ()
}
```

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

Next: [Timeouts, retries & cancellation](timeouts-and-cancellation.md) ·
[Testing your code](testing.md) ·
[Process groups](process-groups.md) ·
[Running commands](commands.md) ·
[Streaming & interactive I/O](streaming.md) ·
[Pipelines](pipelines.md) ·
[Platform support](platform-support.md) ·
[Cookbook](cookbook.md)
