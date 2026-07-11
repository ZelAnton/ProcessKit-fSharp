# ProcessKit — pseudo-terminal (PTY) design

> **Internal engineering design/ADR, not a consumer guide.** This document ratifies the
> architecture of an opt-in PTY mode *before* any code is written, the same way
> [`post-2.0-plan.md`](post-2.0-plan.md) framed the post-2.0 items. It is deliberately kept out
> of the consumer-facing [`docs/`](../README.md) guide index. Nothing here has shipped: PTY is
> still listed under **Not currently supported → Pseudo-terminal (PTY)** in
> [`ROADMAP.md`](../../ROADMAP.md) (lines 71-75). When a stage below ships, it moves that bullet
> into "What the library covers" and adds a consumer guide (`docs/pty.md`) plus the
> matrix rows in [`platform-support.md`](../platform-support.md) — see *Implementation staging*.

Each decision below is **ratified**, not left open — the point of an ADR is to settle the
platform mechanism, the public-API vocabulary, and the honest-result contract before the
implementation diverges per-OS. File/line references are the seams the implementation touches
**as the source stands today** (re-verified against the current tree); they will drift, exactly
as the sibling plan's did, and should be re-confirmed when a stage is picked up.

## Why PTY, and why it is a separate design

ProcessKit wires **pipes** (Windows) / an **`AF_UNIX SOCK_STREAM` socketpair** (POSIX) between
parent and child, never a terminal. `isatty(child_fd)` is therefore always false. A tool that
*demands* a tty — an interactive `ssh` / `sudo` password prompt, a credential helper, a TUI or a
progress bar that switches to line-buffered "dumb" output when it detects a pipe — either misbehaves
or refuses. This is the single largest uncovered area for a process library and a natural growth
direction, but it is too large for one task and forks hard per-platform (Windows ConPTY vs POSIX
`openpty`), so it needs its decisions ratified up front.

The bar is the project's standing one (see the porting methodology in [`ROADMAP.md`](../../ROADMAP.md)
and [`CONTRIBUTING.md`](../../CONTRIBUTING.md)): **the kill-on-dispose tree guarantee is never
weakened, and every platform divergence stays typed or documented — never a silent downgrade.**
A host that cannot give a real PTY returns a typed `ProcessError.Unsupported`; it never quietly
falls back to pipes and pretends.

## Cross-cutting rules (apply to every stage)

Identical in spirit to [`post-2.0-plan.md`](post-2.0-plan.md#cross-cutting-rules-apply-to-every-item);
restated so this document is self-contained:

- **One stage at a time, then stop.** Take a stage to green build + tests + Fantomas +
  `CHANGELOG.md`, then **wait for the user to confirm** before the next. Do not run ahead.
- **Port the contract, not the form.** ProcessKit-rs has no PTY layer, so there is no Rust source to
  transliterate — but the *house* contract still governs: honest results (a non-zero exit is data,
  not a raised error, until the caller asks for success), typed/documented platform divergence, an
  unconditional containment guarantee.
- **Green everywhere.** Warnings-as-errors; tests pass on `ubuntu-latest`, `windows-latest`,
  `macos-latest` and via `scripts/test-linux.ps1`. Native, per-OS work is not "done" until proven
  on all three OSes, not just the dev box.
- **Guard the public surface.** The public-API snapshot test stays green — a PTY stage that adds
  surface updates the approved `PublicApi.*.approved.txt` **intentionally**; internal plumbing must
  not move it.
- **F# specifics.** `.fsproj` compile order is dependency order; indent with spaces; Fantomas is the
  style authority; exception handlers follow the multi-line / justified-swallow rule.
- **Changelog discipline.** Every user-visible stage ships its `CHANGELOG.md` entry in the same
  change set. (This planning doc itself is not user-visible and gets none, exactly as
  `post-2.0-plan.md` got none.)
- **Never log or store secrets.** A PTY carries interactive credentials (that is much of its
  point). argv/env **values** are still never logged/traced/metered/recorded, and — new for PTY —
  captured PTY output and echoed stdin can contain a typed password: the redaction hook on
  `RecordReplayOptions` (`WithRedaction`) must be honored for the merged PTY stream, and the echo
  consequence (below) is documented loudly so a caller does not accidentally capture a secret.

## Decision summary (the ADR, ratified)

| # | Decision | Ratified choice |
|---|----------|-----------------|
| D1 | Opt-in vs implicit | **Opt-in only.** New `Command.Pty(...)` / `Command.pty`; a PTY is never implicit. Default behaviour is byte-identical to today. |
| D2 | Config shape | A `Pty: PtyConfig option` on `CommandConfig` (`None` = off), carrying initial size + flags — not a bare bool, unlike `MergeStderr`/`Setsid`, because a PTY needs parameters. |
| D3 | Output model | A PTY gives the child **one** terminal, so stdout+stderr are physically **one merged stream** at the OS level. PTY therefore *implies* merge semantics; `OutputEvent.Stderr` is never produced under PTY. |
| D4 | Separate-stderr observers | Rejected up front (`ArgumentException`) when combined with `Pty`, reusing the existing `ensureNoMergeStderr` guard family — there is no separate stderr stream to observe. |
| D5 | VT/ANSI sequences | **Preserved verbatim.** ProcessKit never strips or interprets escape sequences; captured output is the raw pty byte stream decoded per the configured encoding. |
| D6 | Resize | New `RunningProcess.ResizeAsync(cols, rows)` → `Task<Result<unit, ProcessError>>`; `ResizePseudoConsole` (Windows) / `TIOCSWINSZ`+`SIGWINCH` (POSIX). On a non-PTY run: typed `ProcessError.Unsupported`. |
| D7 | Containment | **Unchanged guarantee.** PTY changes stdio wiring only. Windows: the Job is attached in the *same* `STARTUPINFOEX` attribute list as the pseudoconsole. POSIX: the pty controlling-tty setup rides the existing helper-launcher pattern; the pgid/cgroup model is untouched. |
| D8 | `Setsid` interaction (POSIX) | **Mutually exclusive.** `Setsid` = new session with *no* controlling tty; `Pty` = new session *with* a controlling pty. Combining them is contradictory → `ArgumentException`. |
| D9 | Unsupported hosts | Typed `ProcessError.Unsupported` (Windows < 10 1809 / no ConPTY; a POSIX host missing the pty devfs or the ctty helper). **Never** a silent downgrade to pipes. |
| D10 | Test doubles | The merged-stream shape is representable by the existing `FakeProcess`/`ScriptedRunner`; `ResizeAsync` is a recorded no-op success on a fake; cassettes gain a `pty` flag (schema bump). A double can never fake `isatty`-true child behaviour — that is inherent and documented. |

The rest of this document is the reasoning and the seams behind each row.

## Platform mechanism — Windows (ConPTY)

### How the current Windows spawn works (the seam)

`spawnWindowsCore` (`src/ProcessKit/Native.Windows.fs:845`) today:

1. Builds anonymous/named pipe pairs for stdin/stdout/stderr (`createAsyncPipePair`), setting the
   child ends into a plain `STARTUPINFO` with `STARTF_USESTDHANDLES`
   (`Native.Windows.fs:926-931`). Under `Command.MergeStderr` it points `hStdError` at the same
   handle as `hStdOutput` (`:914-922`) — the existing OS-level `2>&1`.
2. Calls `CreateProcessW` with `CREATE_SUSPENDED` (`:941-980`).
3. `AssignProcessToJobObject(job, hProcess)` **while still suspended** (`:1002`) so no grandchild
   can escape the container, then `ResumeThread` (`:1010`).
4. `Command.Setsid` on Windows is rejected with `ProcessError.Unsupported "setsid"`
   (`:1056-1060`) — the existing typed-divergence precedent this design follows.

### Ratified ConPTY design

Under `Command.Pty`, the stdio wiring is **replaced**, not extended:

- Create a pseudoconsole with `CreatePseudoConsole(size, hInputRead, hOutputWrite, 0, &hPC)`, fed by
  two pipe pairs: the **parent** keeps the write end of the input pipe (that is the child's tty
  *input* — what interactive stdin writes to) and the read end of the output pipe (the child's tty
  *output* — the single merged stream we capture). New P/Invokes:
  `CreatePseudoConsole` / `ResizePseudoConsole` / `ClosePseudoConsole` (kernel32, Windows 10 1809+).
- Do **not** set `STARTF_USESTDHANDLES` and do **not** pass std handles — a ConPTY child's std
  handles come from the pseudoconsole. This is the fundamental divergence from the pipe path.
- Spawn with `EXTENDED_STARTUPINFO_PRESENT` and a `STARTUPINFOEX` whose `lpAttributeList` is built
  with **two** attributes (`InitializeProcThreadAttributeList` with count 2, then two
  `UpdateProcThreadAttribute` calls):
  - `PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE` = the `hPC` handle.
  - `PROC_THREAD_ATTRIBUTE_JOB_LIST` = the group's Job handle.
- The job-list attribute is the key coexistence decision (D7): the child is **born a Job member**,
  atomically at creation, so the pseudoconsole and the containment guarantee compose without the
  suspended-then-assign dance. (Where a stage must keep the current suspended→assign→resume flow for
  parity, ConPTY tolerates `CREATE_SUSPENDED` alongside the attribute list; but the job-list
  attribute is cleaner and removes the assign race entirely, so it is the ratified path.)

**The conhost sidecar — honest divergence, documented.** `CreatePseudoConsole` spins up a
`conhost.exe`/`OpenConsole.exe` backend process that ProcessKit does *not* own via the Job. It is
an I/O sidecar, not part of the contained user tree: it is bound to the parent's ownership of the
`hPC` handle and dies when `RunningProcess` disposal calls `ClosePseudoConsole`. This is a genuine,
documented divergence (a helper process outside the Job Object) — acceptable because the sidecar has
no user code and its lifetime is deterministic; it goes in the *Caveats* of the future `docs/pty.md`
and a `platform-support.md` note, never left silent.

**Unsupported host.** ConPTY needs Windows 10 build 17763 (1809) or Server 2019. On older Windows,
`Command.Pty` fails the spawn with `ProcessError.Unsupported "Pty (needs Windows 10 1809+ / ConPTY)"`
— probed via a `CreatePseudoConsole` availability check, no silent pipe fallback (D9).

## Platform mechanism — POSIX (`openpty` / `posix_openpt` + `setsid` + controlling tty)

### How the current POSIX spawn works (the seam)

The whole POSIX path is **`posix_spawn`-based** (`spawnPosix`, `src/ProcessKit/Native.Posix.fs:1663`;
core `spawnPosixViaSpawn`). Crucially, **no managed .NET code runs in a forked child** — a
`fork()`-then-managed-code port is unsafe on CoreCLR. Any pre-`exec` setup that `posix_spawn`
attributes cannot express is done through a **helper-process launcher** that self-configures and
then `exec`s the real target *in place* (same pid), so containment/pgid are preserved:

- `Command.Uid`/`Gid` → run through the `setpriv` helper (`Native.Posix.fs:1657-1683`).
- cgroup v2 → a tiny `/bin/sh` launcher writes its own `$$` into `cgroup.procs` then `exec`s the
  target (`Native.Posix.fs:1685-1709`).
- stdio is an `AF_UNIX SOCK_STREAM` socketpair; `Command.Setsid` is `POSIX_SPAWN_SETSID`
  (mutually exclusive with `POSIX_SPAWN_SETPGROUP`, `Native.Posix.fs:1417-1444`), which makes the
  child a session **and** process-group leader (`pgid == pid == sid`) with **no** controlling tty.

### Ratified `openpty` design

Acquiring a controlling terminal is precisely a pre-`exec` step `posix_spawn` cannot express: it is
`setsid()` → `ioctl(slave_fd, TIOCSCTTY, 0)` → `dup2(slave, 0/1/2)` (the `login_tty(3)` sequence).
So PTY on POSIX uses the **same helper-launcher pattern** as `setpriv`/cgroup (D7):

- Parent side: `openpty(&master, &slave, NULL, &termios, &winsize)` (or `posix_openpt` +
  `grantpt` + `unlockpt` + `ptsname` where `openpty` is unavailable). The parent keeps the
  **master** fd — that is the single merged stream, wrapped in a `Socket`/`NetworkStream`-style async
  wrapper exactly like today's socketpair master so the existing `Pump`/streaming machinery is
  untouched. The **slave** becomes the child's stdin/stdout/stderr.
- Child side (no managed code): a small pre-`exec` shim performs `login_tty(slave)` and `exec`s the
  target in place. Two viable shims, ratified in priority order:
  1. **`setsid --ctty` (util-linux)** — `setsid --ctty <program> <args…>` does `setsid()` +
     `TIOCSCTTY` for us; reached through the ordinary `posix_spawn` path with the slave dup'd onto
     0/1/2 via `posix_spawn` file actions. Mirrors the `setpriv` precedent exactly.
  2. **A bundled tiny C-less shim via `/bin/sh`** where `setsid --ctty` is absent (older util-linux,
     macOS/BSD). `sh -c 'exec setsid ...'` is not enough on its own for `TIOCSCTTY`, so on hosts
     lacking `setsid --ctty` the honest answer is a typed `ProcessError.Unsupported` (D9) rather
     than a tty without a controlling terminal — no silent half-measure. (A future stage may ship a
     minimal native helper binary; that is out of scope for the first POSIX stage.)
- **Session/pgid preservation (D7).** `login_tty` calls `setsid()`, so the child is its own
  session+group leader — `pgid == pid == sid`, exactly the invariant `POSIX_SPAWN_SETSID` already
  produces. `killProcessGroup`/`killpg` reaps the whole session-group in teardown unchanged; the
  only difference from `Command.Setsid` is that the child *has* a controlling pty instead of *no*
  controlling tty. That is why `Pty` and `Setsid` are mutually exclusive (D8).
- **cgroup v2 composition.** Nest the pty shim inside the cgroup-join launcher exactly as `setpriv`
  nests today (`Native.Posix.fs:1707-1709`): the launcher joins `cgroup.procs`, then `exec setsid
  --ctty <program>`. The cgroup membership (kernel-enforced) and the controlling-tty setup compose;
  `Mechanism.CgroupV2` is reported unchanged.

**Unsupported host.** A host with no pty support (`openpty`/`/dev/ptmx` missing) or missing the ctty
helper fails the spawn with a typed `ProcessError.Unsupported` naming what PTY needs — never a
socketpair pretending to be a tty.

## Public API shape (ratified)

Additive and opt-in, mirroring the existing immutable-builder vocabulary (`MergeStderr`, `Setsid`):

```fsharp
/// Initial terminal geometry and behaviour flags for a PTY run.
type PtyConfig =
    { Cols: int            // default 80
      Rows: int            // default 24
      Echo: bool }         // default true (OS cooked-mode default); false disables terminal echo

// On Command (member + module function, both, like every other knob):
member Command.Pty : unit -> Command                 // default 80x24, echo on
member Command.Pty : cols: int * rows: int -> Command
member Command.Pty : config: PtyConfig -> Command
val Command.pty : Command -> Command                 // module-level, default geometry

// On CommandConfig:
//   Pty: PtyConfig option    // None = off (the default)

// On RunningProcess (Stage D):
member RunningProcess.ResizeAsync :
    cols: int * rows: int * ?ct: CancellationToken -> Task<Result<unit, ProcessError>>
```

- **`Command.Pty` stores `PtyConfig option`** on `CommandConfig` (D2), threaded to the spawn exactly
  as `MergeStderr`/`Setsid` are (`src/ProcessKit/Command.fs:51,104`).
- **Guards (validated at build time, like the existing `ensureNoMergeStderr` family at
  `Command.fs:189-206`):**
  - `Pty` + `StderrTee`/`OnStderrLine` → `ArgumentException` (D4: no separate stderr under a PTY).
  - `Pty` + `Setsid` → `ArgumentException` (D8: contradictory controlling-tty semantics).
  - `Pty` inside a non-last `Pipeline` stage → rejected, mirroring the existing `MergeStderr`
    pipeline guard (`src/ProcessKit/Pipeline.fs:77-87`): a PTY stage's merged output would inject
    the tty stream into the downstream stage's stdin. A PTY is allowed only as a standalone run or
    the last stage.
  - `Pty` + explicit `MergeStderr` → **allowed and redundant** (PTY already implies merge); no error.
- **`ResizeAsync`** is the only new `RunningProcess` member; on a non-PTY handle it returns
  `Error(ProcessError.Unsupported "Resize (not a PTY run)")` (D6) — honest, not a no-op that lies.

## Output semantics under PTY

- **One merged stream (D3).** A tty is a single bidirectional device; the child's stdout and stderr
  are the same fd. So under `Pty`:
  - `OutputStringAsync`/`OutputBytesAsync` capture that one stream into `ProcessResult.Stdout`;
    `ProcessResult.Stderr` is empty (there is no second stream).
  - `OutputEventsAsync` (`src/ProcessKit/RunningProcess.fs:1081`) yields only
    `OutputEvent.Stdout` events — `OutputEvent.Stderr` (`src/ProcessKit/OutputEvent.fs:22`) is never
    produced. Consumers that discriminate on the tag see everything as stdout, which is honest: the
    OS genuinely did not distinguish them.
  - `StdoutLinesAsync` (`:934`) streams the merged lines. This is the natural, already-correct
    behaviour of the existing merge path — PTY reuses it rather than inventing a parallel one.
- **VT/ANSI preserved verbatim (D5).** ProcessKit does not strip, interpret, or "cook" escape
  sequences — captured bytes are exactly what the child wrote, decoded per encoding. A consumer that
  wants clean text strips VT itself; a consumer driving a TUI wants the raw sequences. Documented so
  a `WaitForLineAsync` predicate author knows a prompt line may carry cursor/color escapes.
- **Newline translation.** A pty applies `ONLCR` (`\n` → `\r\n`) on output by default. The existing
  line-terminator normalization (`src/ProcessKit/LineTerminator.fs`, consumed by `Pump`) already
  strips `\r\n`, so `StdoutLinesAsync` lines come through terminator-clean without special-casing —
  verified against the current line-splitting path.
- **Buffer & streaming policies are unchanged in shape.** `OutputBufferPolicy`
  (`src/ProcessKit/OutputPolicy.fs`, buffered verbs) and `StreamBufferPolicy`
  (`src/ProcessKit/StreamChannel.fs`, streaming verbs, opt-in via `Command.StreamBuffer`) apply to
  the single merged pty stream precisely as they apply to stdout today — same caps, same overflow
  modes, same `RunningProcess.DroppedStreamLineCount` signal. No new policy surface for PTY.

## Readiness probes, interactive stdin, encoding

- **Readiness probes work unchanged.** `WaitForLineAsync` (`RunningProcess.fs:1090`) matches lines
  on the merged stream; `WaitForPortAsync`/`WaitForAsync` never depended on stdio kind, so they are
  entirely unaffected. The one caveat (documented, not code): a prompt line may carry VT escapes, so
  a predicate matching a `sudo` password prompt should match a substring, not an exact string.
- **Interactive stdin writes to the pty master.** `RunningProcess.TakeStdin()`
  (`src/ProcessKit/RunningProcess.fs:552`) returns the `ProcessStdin`
  (`src/ProcessKit/ProcessStdin.fs`: `WriteAsync`/`WriteLineAsync`/`FlushAsync`) whose stream is now
  the pty **master input** side. The child reads it on its tty stdin — this is what finally lets an
  interactive `ssh`/`sudo` prompt be answered.
- **Echo footgun — ratified default + loud docs.** A tty echoes input by default (cooked mode), so
  bytes written via interactive stdin are echoed back into the captured *output* stream. This is
  standard terminal behaviour, not a bug, but it means a typed password can appear in captured
  output. Ratified: ProcessKit leaves the pty in the OS cooked default (`Echo = true`) unless
  `PtyConfig.Echo = false` disables it (the shim sets `ECHO` off in `termios` before `exec`). The
  echo-into-output consequence — and the interaction with the secret-safety invariant — is
  documented prominently in `docs/pty.md` and cross-referenced from the redaction hook.
- **Encoding.** The merged stream is decoded per the existing `StdoutEncoding`
  (default UTF-8; `StderrEncoding` is moot under a merged stream). No PTY-specific encoding rule —
  the platform-support "Default UTF-8 decoding" caveat (`ROADMAP.md:57-58`) applies verbatim; a
  legacy OEM-code-page TUI needs an explicit `StdoutEncoding`, same as today.

## Test doubles

The doubles never touch the OS (`src/ProcessKit.Testing/`), so PTY is representable to the exact
extent that the *observable* contract is representable — and honestly `Unsupported` where a real tty
is genuinely required:

- **`FakeProcess` / `ScriptedRunner`.** `FakeProcess` already builds a real `RunningProcess` over
  in-memory `MemoryStream`s (`src/ProcessKit.Testing/FakeProcess.fs:55-80`). The merged-stream shape
  (D3) is naturally representable: a PTY fake feeds scripted output as `OutputEvent.Stdout` lines and
  an empty stderr. Add `FakeProcess.WithPty(...)`/a merged flag so a test asserts the merged-stream
  and no-`Stderr`-events behaviour, and make `ResizeAsync` on a fake a **recorded no-op success**
  (store the requested `(cols, rows)` for assertions). A double can *not* fake `isatty`-true child
  behaviour (there is no real tty), which is inherent — documented in `testing.md`, not papered over.
- **Cassettes (`RecordReplayRunner`, `src/ProcessKit.Testing/Cassette.fs`).** Byte-exact capture is
  already supported (base64, format v2). PTY adds a `Pty: bool` (and geometry) field to
  `CassetteEntry` — a **schema bump to v3 with back-compat load of v2/v1**, following the exact
  version-policy precedent the record/replay item already set. On replay, reconstruct a merged-stream
  `FakeProcess`. The `WithRedaction` hook (cross-cutting secret rule) scrubs the merged PTY stream
  before it is written, so an echoed password never lands in a committed cassette. Until this stage
  ships, a PTY spawn through a cassette returns honest `ProcessError.Unsupported`, matching how the
  runner already rejects what it cannot faithfully reproduce.

## Support matrix and typed `Unsupported` (no silent downgrade)

New rows for [`docs/platform-support.md`](../platform-support.md) (mirroring its existing capability
matrices, e.g. the Signals matrix at lines 105-108):

| Capability | Windows | Linux | macOS/BSD |
|---|:---:|:---:|:---:|
| `Command.Pty` run | ✅ ConPTY (Win10 1809+) | ✅ `openpty` + `setsid --ctty` | 🟡 needs a ctty helper (see below) |
| `Command.Pty` on an unsupported host | ❌ `ProcessError.Unsupported "Pty (needs … ConPTY)"` | ❌ `Unsupported` (no `/dev/ptmx`) | ❌ `Unsupported` (no ctty helper) |
| `RunningProcess.ResizeAsync` (PTY run) | ✅ `ResizePseudoConsole` | ✅ `TIOCSWINSZ`+`SIGWINCH` | ✅ `TIOCSWINSZ`+`SIGWINCH` |
| `ResizeAsync` on a **non**-PTY run | ❌ `Unsupported` | ❌ `Unsupported` | ❌ `Unsupported` |
| Containment under PTY | ✅ Job Object (job-list attr) | ✅ pgid / cgroup v2 | ✅ pgid |
| conhost sidecar inside the Job | 🟡 no (owned via `hPC`, dies on close) | n/a | n/a |

Every ❌ is a **typed** `ProcessError.Unsupported` returned at spawn (or from `ResizeAsync`), never a
pipe silently substituted for a tty. The macOS 🟡 row is the honest gap: `openpty` exists on macOS,
but the `setsid --ctty` helper does not, so until a native ctty shim ships (a later stage), macOS
PTY is `Unsupported` rather than a controlling-tty-less half-PTY.

## Platform limitations / risks

- **Native, per-OS, two entirely different mechanisms** — the highest-risk area in the library,
  like item 1 of the sibling plan. Ship Windows and POSIX as *separate* stages so a regression is
  bisectable, and prove each on the CI matrix, not just the dev box.
- **conhost sidecar lifetime (Windows).** The sidecar is outside the Job; a bug in `ClosePseudoConsole`
  ordering could leak it. Deterministic teardown coverage is a stage acceptance criterion.
- **CoreCLR post-fork safety (POSIX).** The controlling-tty setup must stay in a helper process /
  `posix_spawn` file actions — no managed code post-`fork` (the standing rule the setpriv/cgroup
  launchers already obey). A `login_tty`-in-managed-child port is explicitly rejected.
- **Deadlock shape.** A PTY merges the child's output; a consumer that stalls while the child floods
  the tty has the same unbounded-backlog shape the streaming verbs already document — pair with a
  `Timeout` and/or `Command.StreamBuffer`. No new footgun, but call it out for PTY specifically.
- **Secret echo.** The single most likely way to violate the secret-safety invariant with PTY (a
  typed password echoed into captured output / a cassette). The `Echo=false` option, the redaction
  hook, and loud docs are the mitigations; every new stage re-verifies them.
- **Windows-version floor.** ConPTY needs 1809+. The typed `Unsupported` on older hosts is a hard
  requirement, tested.

## Implementation staging (independent, separately shippable — future queue candidates)

Each stage is a self-contained, reviewable, green-on-all-OSes increment — a candidate `T-…` queue
task. Ordered so the self-contained Windows path lands first and the risky POSIX ctty path is
isolated.

- **Stage 1 — Core plumbing + Windows ConPTY spawn.** `PtyConfig`, `CommandConfig.Pty`,
  `Command.Pty`/`Command.pty`, the build-time guards (D4/D8, pipeline guard). Windows
  `CreatePseudoConsole`/`ClosePseudoConsole` P/Invokes; `STARTUPINFOEX` attribute list carrying
  `PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE` + `PROC_THREAD_ATTRIBUTE_JOB_LIST` (D7); merged-output
  capture; typed `Unsupported` on pre-1809. Windows-only; POSIX `Pty` returns `Unsupported` in this
  stage. Public-API snapshot updated; `CHANGELOG.md` `### Added`.
- **Stage 2 — POSIX `openpty` spawn (pgid mechanism).** `openpty`/`posix_openpt`; the
  `setsid --ctty` helper-launcher shim; slave dup'd onto 0/1/2 via `posix_spawn` file actions;
  master wrapped in the async stream wrapper; `Setsid`-exclusivity guard (D8); typed `Unsupported`
  where the ctty helper is absent (macOS). Proven on Linux.
- **Stage 3 — cgroup v2 composition (POSIX).** Nest the pty shim inside the cgroup-join launcher so
  `Mechanism.CgroupV2` runs compose with a controlling pty; containment + limits unchanged.
- **Stage 4 — `RunningProcess.ResizeAsync` + `PtyConfig.Echo`.** `ResizePseudoConsole` (Windows) /
  `TIOCSWINSZ`+`SIGWINCH` (POSIX); `Echo` flag wired into `termios`/ConPTY mode; `Unsupported` on
  non-PTY runs (D6).
- **Stage 5 — Test-double support.** `FakeProcess`/`ScriptedRunner` merged-stream + `ResizeAsync`
  no-op; cassette `Pty` field with v3 schema bump and v1/v2 back-compat load; redaction of the merged
  stream. Testing-package public-API snapshot updated.
- **Stage 6 — Docs & roadmap move.** New consumer guide `docs/pty.md`; `platform-support.md` matrix
  rows + the conhost/echo caveats; cookbook recipes; runnable `samples/` (F# + C#); move the PTY
  bullet in `ROADMAP.md` out of "Not currently supported" into "What the library covers". (Doc-only
  bits of earlier stages ship with those stages; this stage is the consolidated guide + roadmap
  move.)

## Definition of done (every stage)

A stage is complete only when **all** hold, then **stop and wait for the user to confirm**:

1. Build green (warnings-as-errors) on the CI matrix and via `scripts/test-linux.ps1`.
2. Tests green on Windows, Linux, macOS — including the stage's own PTY tests (a real tty round-trip
   where the platform supports it; the typed `Unsupported` assertion where it does not).
3. Fantomas clean (`dotnet fantomas --check src tests`).
4. Public-API snapshot reconciled — intentionally updated for the additive surface, untouched by
   internal plumbing.
5. `CHANGELOG.md` entry under `## [Unreleased]` in the correct subsection.
6. Consumer docs updated where the stage is user-visible.
7. Secret-safety re-verified: no argv/env values, and no echoed PTY credential, in any log, trace
   tag, metric tag, or cassette; the redaction hook covers the merged stream.
8. The containment guarantee re-proven under PTY: disposing the run/group reaps the child tree
   (Windows job-list membership; POSIX pgid/cgroup), and the conhost sidecar is torn down.
