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
- [Where the Unix helper binaries come from](#where-the-unix-helper-binaries-come-from)
- [Dropping privileges on Windows](#dropping-privileges-on-windows)
- [PTY echo and captured secrets](#pty-echo-and-captured-secrets)
- [Clearing the inherited environment](#clearing-the-inherited-environment)
- [Secrets in logs, traces, metrics, and cassettes](#secrets-in-logs-traces-metrics-and-cassettes)
- [Putting it together](#putting-it-together)
- [What containment does *not* guarantee](#what-containment-does-not-guarantee)

## The perimeter at a glance

| Threat | Measure | Chapter |
|---|---|---|
| Runaway memory / fork bomb / CPU hog / disk flood | `ProcessGroupOptions` resource limits, including `WithIoMax` | [Process groups](process-groups.md#resource-limits) |
| Log/output flood | `Command.OutputBuffer` / `Command.StreamBuffer` | [Running commands](commands.md), [Streaming](streaming.md#bounding-the-streaming-backlog) |
| Hangs / stuck children | `Command.Timeout` / `IdleTimeout` / `TimeoutGrace` | [Timeouts, retries & cancellation](timeouts-and-cancellation.md) |
| Excess privilege, escaping the containing session (Unix) | `Command.Uid` / `Gid` / `Groups` / `Umask` / `Setsid` | [Running commands](commands.md) |
| Excess privilege, write access to the user's own data (Windows) | `Command.WindowsRestrictedToken` / `WindowsIntegrityLevel` | [Running commands](commands.md) |
| A contained tree reaching into the desktop session (Windows) | `ProcessGroupOptions.WithUiRestrictions` | [Process groups](process-groups.md#windows-ui-restrictions) |
| A credential echoed to a PTY | `PtyConfig.Echo = false` | [Pseudo-terminal (PTY)](pty.md) |
| Leaked inherited secrets in `env` | `Command.EnvClear` | [Running commands](commands.md) |
| Secrets leaking into logs/traces/fixtures | The observability + record/replay secret invariants | [Observability](observability.md), [Testing your code](testing.md#record-and-replay) |
| Command-line injection through a Windows legacy parser | Keep data in ordinary `Arg`/`Args`; never interpolate untrusted input into `WindowsRawArg` | [Running commands](commands.md#windows-raw-command-line-fragments) |
| A hijacked helper binary implementing the hardening itself | Automatic: `setpriv` / `setsid` / `cmd.exe` are pinned to trusted system directories, never taken from `PATH` | [Where the Unix helper binaries come from](#where-the-unix-helper-binaries-come-from) |

> **Windows raw arguments bypass the safe boundary.** `Command.WindowsRawArg`
> appends text directly to the child's Windows command line without quoting. It
> exists only for trusted, fixed fragments required by non-standard parsers.
> User input, environment values, filenames, and other variable data must remain
> ordinary `Arg`/`Args` values; concatenating any of them into a raw fragment is
> command-line injection by construction.

## Whole-tree resource limits

A hostile or merely buggy child can consume unbounded memory, fork until the host
runs out of process table entries, or peg every core. `ProcessGroupOptions`
(`WithMemoryMax` / `WithMaxProcesses` / `WithCpuQuota`) caps the **whole tree**, not
just the direct child, because a memory-bomb or fork-bomb usually isn't the process
you started — it's a descendant. The full builder API, the platform capability
matrix, and the fail-fast behaviour when a cap can't be enforced are in
[Process groups → Resource limits](process-groups.md#resource-limits); the
underlying `ResourceLimits` type lives in `src/ProcessKit/Limits.fs`.

On Linux cgroup v2, add `WithOomGroupKill()` when partial survival after an OOM would leave the
tool in an unsafe or corrupted state. The kernel then treats the cgroup as one OOM unit and kills
the whole tree. The option is deliberately `ProcessError.Unsupported` on Windows and other POSIX
platforms rather than pretending that their different memory-limit semantics are equivalent.

For a disk-flooding child, `ProcessGroupOptions.WithIoMax` adds directional bandwidth and IOPS
ceilings for one explicit target. On Linux, `target` is a cgroup v2 `major:minor` device key and
the four rates (`readBytesPerSecond`, `writeBytesPerSecond`, `readOperationsPerSecond`, and
`writeOperationsPerSecond`) are independent fields written to `io.max`; on Windows, `target` is
the NT volume device name and the Job Object applies one aggregate bandwidth and one aggregate
IOPS ceiling for that volume, so each read/write pair must match. The option overload that takes
`int64` uses zero for an unbounded direction; the option overload uses `None`. Every supplied rate
must be positive, and at least one direction must be bounded.

This is a refusal-or-enforce contract. Linux requires a cgroup v2 hierarchy with the `io` controller
delegated. If cgroup v2 is available but its `io` controller is not, creation and update return
`ProcessError.Unsupported`; the library never creates an unrestricted group as a fallback. Windows
requires the Job Object I/O-rate API; an unavailable API is likewise reported as
`ProcessError.Unsupported`, while an invalid volume or an aggregate read/write mismatch is a
`ProcessError.ResourceLimit`. macOS, BSD, and the POSIX process-group fallback have no whole-tree
I/O controller and always return typed `ProcessError.Unsupported` for this option.

A failed live update restores the previous controller or Job configuration before returning its
typed error; `ProcessGroup.Options.Limits` changes only after the backend confirms the replacement
set. The limit still applies only to the selected device/volume, not to every mounted filesystem,
and it is not a filesystem permission boundary or an encryption mechanism.

The load-bearing fact for a hardening perimeter: caps need a **real container** — a
Windows Job Object or a Linux cgroup v2. On macOS/BSD and the Linux process-group
fallback there is no whole-tree limit primitive at all, so `ProcessGroup.Create`
**fails fast** rather than silently handing back an unbounded group. For ordinary
whole-tree memory/process/CPU-affinity caps this is `ProcessError.ResourceLimit`;
for `WithIoMax` it is the more specific `ProcessError.Unsupported`, because the
platform has no I/O controller concept to attempt. A limit you asked for and didn't
get is a bug you can catch at creation time, not a silent gap discovered during an
incident. Treat either typed error as "this host cannot sandbox this child the way
you asked" and decide accordingly (refuse to run it, or choose a platform with the
required primitive).

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
assume the drop happened. Windows drops privilege a different way; see
[Dropping privileges on Windows](#dropping-privileges-on-windows).

## Where the Unix helper binaries come from

Three Unix features are implemented by **executing a small helper binary** that
does the pre-`exec` work and then `exec`s your program in place (no managed code
may run in a forked .NET child, so this is the only safe mechanism):

| Feature | Helper | What it does before your program runs |
|---|---|---|
| `Uid` / `Gid` / `Groups` | `setpriv` (util-linux) | sets gid, uid, and the supplementary groups |
| `KillOnParentDeath` | `setpriv --pdeathsig` (util-linux), then `/bin/sh` | arms `PR_SET_PDEATHSIG(SIGKILL)`, then checks the parent is still the process that spawned it |
| `Pty` | `setsid --ctty` (util-linux) | new session + acquires the pty as controlling terminal |

`KillOnParentDeath` needs the second step because `setpriv` can only arm the
signal *inside* the child, after the spawn: a parent that dies in that moment is
never covered by the arming (the kernel reparents the orphan first, and the
signal then binds to whatever adopted it). The `/bin/sh` step runs immediately
after the arming and before your program, and compares the child's current parent
with the pid captured before the spawn — equal, it `exec`s your program in place;
different, it `SIGKILL`s itself and your program never runs. It is pinned to the
absolute `/bin/sh` for the same reason the two util-linux helpers are pinned; a
host without it fails the spawn with a typed `ProcessError.Spawn` rather than
arming with that window left open.

The helper is the thing that **performs** the hardening, so it is exactly the
thing an attacker would want to replace — and on the privilege-drop path it runs
**as root**, before the credentials it exists to lower have been lowered. A
`setpriv` found through the calling process's `PATH` would therefore be an
attacker-chosen program executed with the parent's full privileges.

**So neither helper is ever resolved on `PATH`.** ProcessKit looks each one up
only in a fixed list of trusted system directories — `/usr/bin`, `/bin`,
`/usr/sbin`, `/sbin`, in that order — and launches the **absolute path** of the
match, which means no `PATH` entry participates anywhere along the chain (`exec`
of a path-form program performs no search). This is the same stance Windows
already takes for the `.cmd`/`.bat` wrapper's shell, which comes from the system
directory rather than `PATH`/`%ComSpec%`. `Command.PreferLocal` does not apply
either: it substitutes *your* target program, never the helper that launches it.

Three consequences worth knowing:

- **A host with no helper in a trusted directory fails honestly**, exactly as a
  host missing the tool outright always did: `ProcessError.Spawn` naming the knob
  that needed `setpriv`, `ProcessError.Unsupported` for `Pty`. It never falls back
  to a `PATH` copy, and it never runs your program with the hardening quietly
  skipped. The typed error is the signal — handle it as "this host cannot apply
  this measure".
- **Non-FHS layouts are affected.** Distributions that do not install util-linux
  into those directories (NixOS, Guix, some minimal images) get that typed failure
  even though `setpriv` is on their `PATH`. Mainstream Linux does install both
  helpers into a trusted directory — but not always the *same* one, which is why
  the list carries `/usr/bin` **and** `/bin`. Debian/Ubuntu and Fedora put both
  under `/usr/bin`; Alpine's `util-linux` package puts `setsid` under `/usr/bin`
  and **`setpriv` under `/bin`** (verified in
  `mcr.microsoft.com/dotnet/sdk:10.0-alpine`, the image this project's own Alpine
  CI leg uses). On a merged-`/usr` host the two paths resolve to one directory, so
  `/bin` can look redundant there — it is not: dropping it would break privilege
  dropping and `KillOnParentDeath` on Alpine/musl. When validating your own image,
  check for the helper in **any** of the four directories, not just `/usr/bin`.
- **This is a resolution boundary, not a filesystem-integrity check.** It assumes
  the trusted directories themselves are writable only by root, which is the
  assumption every other program on the host already makes. ProcessKit does not
  verify their ownership or mode, and it cannot help a host where `/usr/bin` is
  already compromised.

## Dropping privileges on Windows

Windows has no `setuid`, so the Unix family above cannot be ported knob for knob.
What it has instead is the **token**: every process carries one, and a child can be
given a *weakened copy of the caller's own* rather than the caller's own. That is
the same goal — a child that cannot do what the parent could — reached through a
different primitive, and it is what closes what used to be a one-sided chapter here.

Three Windows-only measures, each covering a different axis:

- **`Command.WindowsRestrictedToken()` — take away what the child may *do*.** The
  child runs under a token created with `CreateRestrictedToken(DISABLE_MAX_PRIVILEGE)`:
  the caller's identity and ACLs, but no privilege beyond the always-present
  `SeChangeNotifyPrivilege`. This matters most when the caller is (or may be)
  elevated — an untrusted child inheriting an administrator token can debug other
  processes, load drivers, take ownership, and shut the host down.
- **`Command.WindowsIntegrityLevel(level)` — take away what the child may *write
  to*.** Lowering the child's mandatory integrity level (`Medium` / `Low` /
  `Untrusted`) makes Windows' no-write-up policy deny it write access to everything
  labelled above that level — the user's profile, `HKCU`, other processes' windows —
  whatever the DACL says. `Low` is the practical sandbox level; `Untrusted` is
  stricter than most programs can survive, so treat it as a tested choice rather
  than a stricter default.
- **`ProcessGroupOptions.WithUiRestrictions(...)` — take away what the *tree* may do
  to the desktop session.** A Job Object UI restriction set (clipboard read/write,
  desktop creation/switching, display and system parameters, global atoms, and
  `ExitWindows`) applied to the whole contained tree, not just the direct child. The
  flags and their exact meanings are in
  [Process groups → Windows UI restrictions](process-groups.md#windows-ui-restrictions).

**F#**

```fsharp
task {
    // Windows: no privileges, no write access above Low integrity, and no reach into the desktop session.
    let options =
        ProcessGroupOptions()
            .WithMaxProcesses(32)
            .WithUiRestrictions(WindowsUiRestrictions.All)

    match ProcessGroup.Create options with
    | Error err -> eprintfn $"cannot sandbox this host: {err.Message}" // Unsupported off Windows
    | Ok group ->
        use group = group

        let untrusted =
            Command.create "untrusted-tool"
            |> Command.envClear
            |> Command.windowsRestrictedToken
            |> Command.windowsIntegrityLevel WindowsIntegrityLevel.Low
            |> Command.timeout (TimeSpan.FromSeconds 30.0)

        match! group.StartAsync untrusted with
        | Ok proc ->
            use proc = proc
            let! outcome = proc.WaitAsync()
            printfn $"{outcome}"
        | Error err -> eprintfn $"{err.Message}"
}
```

The same honesty rules apply in the other direction: on **POSIX**,
`WindowsRestrictedToken`/`WindowsIntegrityLevel` fail the spawn — and
`WithUiRestrictions` fails `ProcessGroup.Create`/`UpdateLimits` — with
`ProcessError.Unsupported`, never a silent no-op. Because each half of the pair is
unsupported on the platform the other needs, combining a Windows token knob with a
Unix `Uid`/`Gid`/`Groups`/`Umask`/`Setsid` knob on one command is rejected at the
**builder boundary** (`ArgumentException`) rather than left to fail at runtime on
every host: build the command your platform can actually run, branching on
`RuntimeInformation.IsOSPlatform` (or `ProcessGroup.Mechanism`) where you need both.

Two limits worth stating plainly, so this is not mistaken for more than it is. The
child keeps the **caller's identity**: a restricted, low-integrity child can still
*read* everything the caller can read, and can still open network connections — this
is privilege reduction, not isolation, and a secret readable by the caller is
readable by the child (which is why `EnvClear`, below, still matters). And the
already-open stdio handles keep working at any integrity level, because their access
check happened in the parent — by design, or the child could not report anything back.

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
  of the match key) redacts override *values* by construction — what it puts in
  the file is only the variable names and a SHA-256 fingerprint. But `program`,
  `args`, `stdout`, `stderr`, and — for a call recorded as a typed **failure** —
  that failure's own streams, detail, JSON-RPC `data`, and the `PATH` a
  `NotFound` searched (the one place an environment *value* is stored verbatim,
  and it is the child's effective `PATH`, so an `Env("PATH", …)` override lands
  there too) are kept **verbatim** by default and can carry secrets (a
  `--password=…` argument, a token echoed to output) — scrubbing those needs
  the opt-in [`RecordReplayOptions.WithRedaction`](testing.md#record-and-replay)
  hook (applied to a string capture's stdout/stderr, a bytes capture's stderr,
  and every one of those failure fields; a raw `byte[]` stdout capture is stored
  opaquely and is *not* passed through the redactor). A
  PTY recording's merged stream goes through the same `WithRedaction` hook, which
  is how an echoed credential is kept out of a PTY cassette even with
  `PtyConfig.Echo = true`. The **output-wiring** fingerprint (the other half of the
  match key) follows the environment fingerprint's rule rather than the verbatim
  one: a `StdoutToFile`/`StderrToFile` redirect path is folded in as a SHA-256
  digest, so a redirect target never reaches the file in clear text. Review any
  fixture recorded from an untrusted or
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

await using var proc = started.GetValueOrThrow();
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
