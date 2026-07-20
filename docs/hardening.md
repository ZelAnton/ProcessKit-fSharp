# Hardening untrusted children

[Previous: Overview](./)

Running a child you don't fully trust — a build plugin, a user-supplied script, a
tool downloaded at install time — is a different problem from running your own
tooling: the child may try to consume unbounded memory or CPU, fork until the host
falls over, flood your logs, hang forever, read secrets out of its own environment,
or leave an echoed password sitting in a log or a test fixture. ProcessKit already
has a piece for each of these; this guide draws them into one perimeter instead of
leaving you to rediscover them one incident at a time.

Every measure below already has its own chapter — this page cites, not repeats, the
authoritative description and platform caveats. Read the linked chapter before
relying on a mechanism in production; the platform matrices here are summaries, not
the source of truth.

- [The perimeter at a glance](#the-perimeter-at-a-glance)
- [Whole-tree resource limits](#whole-tree-resource-limits)
- [Capping the output flood](#capping-the-output-flood)
- [Timeouts: total, idle, and graceful](#timeouts-total-idle-and-graceful)
- [Dropping privileges and detaching the session](#dropping-privileges-and-detaching-the-session)
- [PTY echo and captured secrets](#pty-echo-and-captured-secrets)
- [Clearing the inherited environment](#clearing-the-inherited-environment)
- [Secrets in logs, traces, metrics, and cassettes](#secrets-in-logs-traces-metrics-and-cassettes)
- [Putting it together](#putting-it-together)
- [What containment does *not* guarantee](#what-containment-does-not-guarantee)

## The perimeter at a glance

| Threat | Measure | Chapter |
|---|---|---|
| Runaway memory / fork bomb / CPU hog | `ProcessGroupOptions` resource limits | [Process groups](process-groups.md#resource-limits) |
| Log/output flood | `Command.OutputBuffer` / `Command.StreamBuffer` | [Running commands](commands.md), [Streaming](streaming.md#bounding-the-streaming-backlog) |
| Hangs / stuck children | `Command.Timeout` / `IdleTimeout` / `TimeoutGrace` | [Timeouts, retries & cancellation](timeouts-and-cancellation.md) |
| Excess privilege, escaping the containing session | `Command.Uid` / `Gid` / `Groups` / `Umask` / `Setsid` | [Running commands](commands.md) |
| A credential echoed to a PTY | `PtyConfig.Echo = false` | [Pseudo-terminal (PTY)](pty.md) |
| Leaked inherited secrets in `env` | `Command.EnvClear` | [Running commands](commands.md) |
| Secrets leaking into logs/traces/fixtures | The observability + record/replay secret invariants | [Observability](observability.md), [Testing your code](testing.md#record-and-replay) |

## Whole-tree resource limits

A hostile or merely buggy child can consume unbounded memory, fork until the host
runs out of process table entries, or peg every core. `ProcessGroupOptions`
(`WithMemoryMax` / `WithMaxProcesses` / `WithCpuQuota`) caps the **whole tree**, not
just the direct child, because a memory-bomb or fork-bomb usually isn't the process
you started — it's a descendant. The full builder API, the platform capability
matrix, and the fail-fast behaviour when a cap can't be enforced are in
[Process groups → Resource limits](process-groups.md#resource-limits); the
underlying `ResourceLimits` type lives in `src/ProcessKit/Limits.fs`.

The load-bearing fact for a hardening perimeter: caps need a **real container** — a
Windows Job Object or a Linux cgroup v2. On macOS/BSD and the Linux
process-group fallback there is no whole-tree limit primitive at all, so
`ProcessGroup.Create` **fails fast** with `ProcessError.ResourceLimit` rather than
silently handing back an unbounded group — a limit you asked for and didn't get is
a bug you can catch at creation time, not a silent gap discovered during an
incident. Treat a caught `ProcessError.ResourceLimit` as "this host cannot sandbox
this child the way you asked" and decide accordingly (refuse to run it, or accept
the narrower containment the platform *can* offer).

## Capping the output flood

A hostile child can also try to exhaust memory a different way: by printing without
end. Two independent policies bound that, for the two different ways you consume
output:

- **Captured runs** (`OutputStringAsync`, `RunAsync`, …) — `Command.OutputBuffer`
  bounds retained lines/bytes while still fully draining the pipe (the child never
  blocks). See the buffer-policy section of [Running commands](commands.md) for
  `OutputBufferPolicy.Bounded` / `.WithMaxBytes` / `OverflowMode.Error` and how
  `ProcessResult.Truncated` / `ProcessError.OutputTooLarge` report an overflow.
- **Streamed runs** (`StdoutLinesAsync`, `OutputEventsAsync`, `WaitForLineAsync`) —
  `Command.StreamBuffer` bounds the in-flight channel backlog with
  `StreamBufferPolicy.Bounded` and a `StreamFullMode` (`Backpressure`, `DropOldest`,
  `DropNewest`, `Error`). See
  [Streaming → Bounding the streaming backlog](streaming.md#bounding-the-streaming-backlog).

Neither policy bounds *wall time* — a flood that never stops still needs a
[timeout](#timeouts-total-idle-and-graceful) to actually end the run.
`Backpressure` (the streaming default under `Bounded`) is safe only against a
*trusted* producer: against a hostile one, a full channel just makes the child
block writing to its own stdout, which is fine for containment but does not free
you from needing a deadline too.

## Timeouts: total, idle, and graceful

An untrusted child may simply never exit, or exit-then-hang a descendant. Three
independent knobs on `Command`, fully covered in
[Timeouts, retries & cancellation](timeouts-and-cancellation.md):

- **`Timeout(duration)`** bounds the run's total wall time and kills the whole tree
  at the deadline — the baseline every hardened run should set.
- **`IdleTimeout(duration)`** kills the tree when neither stdout nor stderr has
  produced output for `duration`, independent of `Timeout` — useful against a child
  that is alive but stuck, which a total timeout alone would still have to wait out.
- **`TimeoutGrace(grace)`** turns the default hard kill into `SIGTERM` → wait up to
  `grace` → `SIGKILL`, letting a *cooperative* child clean up. Skip it for a
  genuinely hostile child you don't trust to honor `SIGTERM` promptly — the plain
  hard kill is the safer default, and either way there is no signal tier on
  Windows (a deadline there kills the Job Object atomically; `TimeoutGrace` is
  accepted but has no effect).

A timed-out run reports `Outcome.TimedOut` (captured verbs) or
`ProcessError.Timeout` (success-checking verbs) — never a silent partial result —
so a caller sandboxing untrusted work can always tell a deadline kill apart from a
normal exit.

## Dropping privileges and detaching the session

Running an untrusted child under the caller's own identity hands it everything that
identity can do. On **Unix**, `Command.Uid` / `Gid` / `Groups` / `Umask` /
`Setsid` (the common pair is `User(uid, gid)`) drop it to a least-privileged
identity before `exec`; the full contract — the `setpriv` mechanism, the root-only
gate on dropping privileges, why `Groups` needs an accompanying `Uid`/`Gid`, and
`Umask`'s file-creation-mask semantics — is in
[Running commands → Unix privilege drop & session detach](commands.md). Two facts
worth having at hand specifically for a hardening review:

- A `Uid`/`Gid` drop **clears** the parent's supplementary groups by default, so a
  child dropped to a service account does **not** inherit whatever groups the
  *caller* happened to hold — pass `Groups(gids)` explicitly if the target account
  needs specific supplementary group membership, or `Groups([])` to keep the
  cleared default visible at the call site.
- `Setsid()` detaches the child into its own session, which is good isolation from
  the caller's controlling terminal, but see
  [What containment does *not* guarantee](#what-containment-does-not-guarantee) for
  the containment implication on the POSIX process-group backend.

This whole family is **Unix-only**: on Windows, any of `Uid`/`Gid`/`Groups`/
`Setsid`/`Umask` fails the spawn with `ProcessError.Unsupported` — never a silent
no-op — so a cross-platform hardening path must handle that error rather than
assume the drop happened. Windows has no equivalent primitive to substitute; rely
on the Job Object's resource limits and the caller's own least-privileged service
account instead.

## PTY echo and captured secrets

A `Command.Pty(config)` run gives the child a real terminal, which some
interactive tools (an `ssh`/`sudo`-style password prompt) demand before they will
accept sensitive input at all. Setting `PtyConfig.Echo = false` (default is
`true`) disables the terminal's cooked-mode echo, so a secret written to the
child's stdin through the PTY is not copied back into the captured/streamed
output. The full recipe (keep stdin open, write the secret only after the child
starts, close stdin to finish the prompt) is in
[Pseudo-terminal (PTY) → Password-style prompt without echoing the secret](pty.md#password-style-prompt-without-echoing-the-secret).

**Platform caveat:** `Echo = false` is a POSIX PTY slave setting. On **Windows**,
echo is controlled by the *child's own* console mode, and ConPTY has no supported
parent-side way to force it off before the child starts — so a Windows password
prompt must suppress its own echo, and `PtyConfig.Echo = false` is not a Windows
guarantee. Never rely on echo suppression alone to keep a secret out of a log or a
test fixture — see the next section.

## Clearing the inherited environment

By default a child inherits the caller's full environment, which for an untrusted
child means every secret sitting in that environment (API tokens, cloud
credentials, `.netrc`-adjacent variables) is handed over too, whether or not the
child needs it. `Command.EnvClear` starts the child from an **empty** environment
instead; add back only the variables the child actually needs with `Env`. There is
deliberately no allow-list/inherit-subset mode — `EnvClear` then `Env` keeps the
final set explicit and visible at the call site (see
[Running commands](commands.md)).

## Secrets in logs, traces, metrics, and cassettes

Two independent secret-safety invariants matter for a hardening review, and they
are **not** the same guarantee — read both before assuming argv/output is safe
everywhere:

- **Observability never sees argv or env values.** Across all three diagnostic
  channels (`ILogger`, `Activity` tracing, `Meter` metrics), only the program
  *name* and non-secret facts (pid, outcome, durations, exit code/signal, retry
  counts) are ever emitted — argv and environment **values** never reach a log
  message, a trace tag, or a metric tag. See
  [Observability](observability.md).
- **Cassettes are more selective — verify what's actually redacted before
  committing a fixture.** `RecordReplayRunner`'s environment **fingerprint** (part
  of the match key) redacts override *values* by construction — only variable
  names and a SHA-256 fingerprint are ever written to a cassette file. But
  `program`, `args`, `stdout`, and `stderr` are stored **verbatim** by default and
  can carry secrets (a `--password=…` argument, a token echoed to output) —
  scrubbing those needs the opt-in
  [`RecordReplayOptions.WithRedaction`](testing.md#record-and-replay) hook (applied
  to a string capture's stdout/stderr and a bytes capture's stderr; a raw `byte[]`
  stdout capture is stored opaquely and is *not* passed through the redactor). A
  PTY recording's merged stream goes through the same `WithRedaction` hook, which
  is how an echoed credential is kept out of a PTY cassette even with
  `PtyConfig.Echo = true`. Review any fixture recorded from an untrusted or
  credential-bearing run before committing it, and keep secret-bearing cassette
  files out of world-readable locations (on Unix they are written owner-only,
  `0600`; on Windows a cassette inherits the containing directory's ACL). See
  [Testing your code → Record and replay](testing.md#record-and-replay) for the
  full cassette contract.

## Putting it together

A representative sandbox for one untrusted tool: a resource-limited group,
privileges dropped and the session detached, a hermetic environment, bounded
output, and both a total and an idle deadline.

**F#**

```fsharp
task {
    let options =
        ProcessGroupOptions()
            .WithMemoryMax(256L * 1024L * 1024L) // 256 MiB whole-tree ceiling
            .WithMaxProcesses(32)                 // fork-bomb ceiling
            .WithCpuQuota(1.0)                    // one core

    match ProcessGroup.Create options with
    | Error err -> eprintfn $"cannot sandbox this host: {err.Message}" // ProcessError.ResourceLimit
    | Ok group ->
        use group = group

        let untrusted =
            Command.create "untrusted-tool"
            |> Command.envClear // no inherited secrets in the child's environment
            |> Command.user 1000 1000
            |> Command.groups [] // explicit: no supplementary groups granted back
            |> Command.setsid
            |> Command.umask 0o077
            |> Command.timeout (TimeSpan.FromSeconds 30.0)
            |> Command.idleTimeout (TimeSpan.FromSeconds 10.0)
            |> Command.timeoutGrace (TimeSpan.FromSeconds 5.0)
            |> Command.outputBuffer ((OutputBufferPolicy.Bounded 2000).WithMaxBytes(4 * 1024 * 1024))

        match! group.StartAsync untrusted with
        | Ok proc ->
            use proc = proc
            let! outcome = proc.WaitAsync()
            printfn $"{outcome}"
        | Error err -> eprintfn $"{err.Message}"
}
```

**C#**

```csharp
var options = new ProcessGroupOptions()
    .WithMemoryMax(256L * 1024L * 1024L) // 256 MiB whole-tree ceiling
    .WithMaxProcesses(32)                 // fork-bomb ceiling
    .WithCpuQuota(1.0);                   // one core

var created = ProcessGroup.Create(options);
if (created is { IsOk: false, ErrorValue: var groupErr })
{
    Console.Error.WriteLine($"cannot sandbox this host: {groupErr.Message}"); // ProcessError.ResourceLimit
    return;
}

using var group = created.GetValueOrThrow();

var untrusted = new Command("untrusted-tool")
    .EnvClear() // no inherited secrets in the child's environment
    .User(1000, 1000)
    .Groups(Array.Empty<int>()) // explicit: no supplementary groups granted back
    .Setsid()
    .Umask(0b000_111_111) // C# has no octal literal; this is 0o077 grouped as 3-bit octal digits
    .Timeout(TimeSpan.FromSeconds(30))
    .IdleTimeout(TimeSpan.FromSeconds(10))
    .TimeoutGrace(TimeSpan.FromSeconds(5))
    .OutputBuffer(OutputBufferPolicy.Bounded(2000).WithMaxBytes(4 * 1024 * 1024));

var started = await group.StartAsync(untrusted);
if (started is { IsOk: false, ErrorValue: var startErr })
{
    Console.Error.WriteLine(startErr.Message);
    return;
}

using var proc = started.GetValueOrThrow();
var outcome = await proc.WaitAsync();
Console.WriteLine(outcome);
```

None of these builders are mutually exclusive, and none of them substitute for
another — each closes a different gap. Skipping the resource limits still leaves
you protected against a stuck child (timeouts) but not against a memory bomb, and
so on.

## What containment does *not* guarantee

Every guarantee above is honest — but honest also means naming where it stops:

- **A `setsid` descendant can escape the POSIX process-group mechanism.** On the
  POSIX process-group backend (macOS/BSD always, Linux without a delegated cgroup
  v2 hierarchy), teardown works by signalling tracked process-group ids. A
  descendant that itself calls `setsid()` — deliberately, to survive its parent —
  starts a **new** process group the containing `ProcessGroup` never tracked, so it
  can outlive teardown. This is a real gap in that specific mechanism, not a
  documentation nuance: the Job Object and cgroup v2 mechanisms have no such hole,
  because their membership is kernel-enforced rather than pgid bookkeeping. Check
  `ProcessGroup.Mechanism` when this matters, and see
  [Platform support → Caveats](platform-support.md#caveats) for the full writeup.
- **There is no whole-tree resource limit on macOS/BSD, or on Linux without a real
  cgroup v2 root.** As covered in
  [Whole-tree resource limits](#whole-tree-resource-limits), `ProcessGroup.Create`
  refuses to silently hand back an unbounded group when you asked for limits it
  can't enforce — but that means the *absence* of `ProcessError.ResourceLimit`
  is your only signal that a cap actually applies; there is no partial-enforcement
  mode to fall back to on these hosts.
- **cgroup v2 limits need the real hierarchy root, not just any Linux host.**
  Enabling the cgroup v2 controllers a limit needs is only permitted at the
  hierarchy's true root; a cgroup *namespace* root — what an ordinary Docker
  container or a systemd-managed scope/service sees — does not qualify, and
  `ProcessGroup.Create` reports `ProcessError.ResourceLimit` there too. See
  [Running in containers](containers.md) for what this means for a containerized
  deployment specifically.
- **A shared `ProcessGroup`'s per-run `Timeout` is not atomic on Windows.** If you
  sandbox several untrusted children in one shared group rather than giving each
  its own (private-group) run, a per-run `Timeout`/`CancelOn` on Windows hard-kills
  only that run's leader process — a descendant that inherited the leader's stdout
  pipe can keep the capture from returning until it exits or the whole group is
  torn down. For a hard per-run deadline on Windows, give each untrusted child its
  own group (the default one-shot behaviour) rather than sharing one; see the
  [Process groups](process-groups.md#putting-processes-in) capture-normalization
  note.

---

Next: [Running in containers](containers.md)
