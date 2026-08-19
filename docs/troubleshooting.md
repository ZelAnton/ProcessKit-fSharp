# Troubleshooting

[Previous: Overview](./)

Start with the symptom below, then follow the linked chapter for the full API
contract and platform details. Preserve the complete `ProcessError.Message` or
`Outcome.Unobserved` reason when collecting diagnostics.

A `ProcessError.Message` is written for a human and may be reworded between
releases, so do not match on it. When a triage script or a log pipeline has to
classify a failure or an outcome mechanically, key on the stable identifier of
its case instead — `not_found`, `resource_limit`, `timed_out`, and the rest. The
canonical list of those identifiers is the generated dictionary
`spec/identifiers.json` in the repository, described under
[Stable identifiers](jsonl-reports.md#stable-identifiers); it is the source of
truth, and the names in it are additive and never renamed.

## Mojibake or garbled captured output

**Symptom:** Captured text contains `�`, accented characters are wrong, or output
looks correct in a terminal but not in `ProcessResult.Stdout`.

**Cause:** ProcessKit decodes captured output as UTF-8 by default. Some Windows
console programs still write the active OEM code page, and older tools may write
Windows code page 1252. Decoding those bytes as UTF-8 produces mojibake.

**Solution:** For a Windows console program, `ConsoleEncoding()` is the whole fix:
it resolves the code page this host's console actually uses — or the system OEM
code page when the process has no console — and decodes both streams with it. It
registers the code-page provider for you, and off Windows it is a no-op.

```fsharp
let command = (Command.create "legacy-tool").ConsoleEncoding()
```

When the child writes something other than the console encoding — a fixed code
page such as Windows-1252, or a UTF-16 tool — name it explicitly instead, per
stream with `StdoutEncoding` / `StderrEncoding` when they differ or `Encoding` for
both:

```fsharp
open System.Text

Encoding.RegisterProvider CodePagesEncodingProvider.Instance

let fixedCodePage =
    Command.create "legacy-tool"
    |> Command.stdoutEncoding (Encoding.GetEncoding 1252)
```

The `RegisterProvider` call is needed only on this explicit path: single-byte code
pages such as 1252 are not built into `System.Text.Encoding`, and unlike
`ConsoleEncoding()` nothing has registered the provider on your behalf. The
complete decoding behavior and both F# and C# APIs are in
[Running commands: Encodings](commands.md#encodings).

## Process hangs during or after execution

**Symptom:** The child stops making progress, or it exits but the parent still
waits for output completion.

**Cause:** A child writing to a pipe eventually blocks if nobody drains that
pipe. Waiting for exit before consuming stdout or stderr creates backpressure:
the child waits for pipe space while the parent waits for the child. A live
`RunningProcess` can also remain incomplete when a streaming consumer is
abandoned without finishing or disposing the run.

**Solution:** Let a one-shot verb such as `OutputStringAsync` drain both streams,
or start consuming a live stream immediately. After
`StdoutLinesAsync` or `OutputEventsAsync` completes, call `FinishAsync` to obtain
the outcome and drained stderr. If output is irrelevant, `WaitAsync` drains and
discards it. Always dispose the handle.

Set `Command.Timeout` as a final wall clock bound. At the deadline ProcessKit
kills the tree and closes its pipes, but a timeout is a safety bound, not a
replacement for consuming output. See [Streaming lifecycle](streaming.md#lifecycle)
and [Timeouts, retries and cancellation](timeouts-and-cancellation.md).

**A second cause, bounded for you on a single-command run:** the child spawned
something that inherited its stdout/stderr and outlived it, so the pipe still has
a writer and never reaches end-of-file. For a run driven through one
`RunningProcess` handle — every `Command`/`Exec` capture verb, and every streaming
or interactive session — ProcessKit gives the pumps a short window after the
child's exit status is known, then closes its own read ends and returns that
outcome with `Truncated` set, so this shows up as an incomplete capture rather
than a hang, with no `Command.Timeout` needed. See
[Output a descendant keeps open](streaming.md#output-a-descendant-keeps-open).

A **pipeline** does not have that bound yet: `Pipeline`'s buffered verbs wait for
the last stage's stdout and for every stage's stderr to reach end-of-file, so a
stage that leaves a background job holding one of them (`sh -c 'daemon & echo hi'`)
still hangs exactly as described above. The whole-chain
[`Pipeline.Timeout`](pipelines.md#timeouts-and-cancellation) does not rescue that
one either: the deadline is disarmed the moment every stage is terminal, which is
exactly when this wait begins. Cancel the run through its `CancellationToken` (that
*does* tear the chain's group down, descendants included), or keep the descendant off
the stage's own stdout/stderr in the first place (`daemon >/dev/null 2>&1 &`).

## Deadlock behavior with `StreamBuffer`

**Symptom:** A streamed run stops after exactly the configured backlog capacity,
often while the consumer is waiting for another child action or for more input.

**Cause:** `StreamFullMode.Backpressure` is lossless by blocking the ProcessKit
pump when the bounded channel is full. The operating system pipe then fills and
blocks the child. A deadlock results if the consumer will not read until the
child performs an action it cannot reach while blocked on output.

**Solution:** Keep consuming concurrently, increase the capacity, or choose
`DropOldest`, `DropNewest`, or `Error` when loss or an explicit failure is safer
than pacing the producer. Add a total `Timeout`, especially for an untrusted or
unbounded producer. The policy tradeoffs are in
[Bounding the streaming backlog](streaming.md#bounding-the-streaming-backlog);
the defensive configuration is summarized in
[Hardening untrusted children](hardening.md#capping-the-output-flood).

## Zombie or orphaned processes

**Symptom:** An operating system tool shows a remaining child, or a result ends
with `Outcome.Unobserved reason`.

**Cause:** Read the `Unobserved` reason: it says which case this is, and the
cases differ in whether the child can still be running. The two you are most
likely to see:

- *The tree was hard-killed but not reaped in time* — the reason names the
  bounded post-kill reap window. A wait on a handle whose tree was hard-killed
  through `RunningProcess.Kill()`, and a `StopAsync` whose grace window escalated
  to a hard kill, wait at most a post-kill budget for that reap to land
  (5 seconds; `StopAsync` gets its grace period plus that budget). When the
  window elapses the verb reports `Unobserved`, and the child *may still be
  alive* — for example wedged in uninterruptible (`D`-state) sleep, which defers
  even `SIGKILL` until its I/O unblocks. The wait is transferred, not dropped: a
  background reaper holds the single remaining right to wait for and reap that
  tree, so it is still reaped exactly once when the kernel lets it die. A fired
  timeout and a cancelled run bound the same reap but keep their own answer,
  `Outcome.TimedOut` and `ProcessError.Cancelled`.
- *The status read itself failed* — most other reasons. The process concluded,
  but ProcessKit could not obtain a trustworthy exit status for it, usually
  because a native wait failed or a POSIX reap race could not be resolved.

An actual orphan — a live process nothing owns any more — is a third, separate
case, more commonly caused by losing ownership of a live handle, starting
descendants outside ProcessKit, or the parent being killed so abruptly that
disposal cannot run.

**Solution:** For a lost or never-disposed handle, keep `RunningProcess` and
`ProcessGroup` in `use` or `await using` scope and drive each run through a
terminal verb. ProcessKit's kill on drop ownership kills and reaps the contained
tree during normal disposal, and the finalizer is a fallback.

That is not the fix for the post-kill case: the kill has already been delivered
and the remaining wait already belongs to the background reaper, so disposing or
killing again cannot make the reap land sooner. Look instead at what the child is
blocked in — on Linux, `ps -o stat= -p <pid>` reports `D` for uninterruptible
sleep — and at the storage, device, or network filesystem it was using; the tree
is reaped once that I/O completes. Preserve the `Unobserved` reason if it recurs.

Sudden parent death is a separate platform concern; use
`Command.KillOnParentDeath` only after reading its scope in the
[platform capability matrices](platform-support.md#capability-matrices). The
remaining containment gaps are summarized in
[Hardening untrusted children](hardening.md#what-containment-does-not-guarantee).
Container PID 1 and processes created outside ProcessKit are covered in
[Running in containers](containers.md#running-as-pid-1).

## Spawn failures with specific error codes

**Symptom:** A command is found but returns `ProcessError.Spawn`, or it exits
immediately with an operating system status.

**Cause:** `Spawn.Detail` carries the native failure text. Interpret it on the
host where it occurred:

| Platform code | Usual meaning | What to check |
|---|---|---|
| Windows `2` (`0x2`) | File not found | Resolved program path, working directory, and `PATH` |
| Windows `5` (`0x5`) | Access denied | File permissions, policy, and security software |
| Windows `193` (`0xC1`) | Bad executable format | Corrupt file, script passed as an executable, or wrong binary format |
| Windows `216` (`0xD8`) | Machine type mismatch | Binary and operating system architecture |
| Windows `740` (`0x2E4`) | Elevation required | Application manifest and caller privilege |
| Windows `0xC0000135` (signed `-1073741515`) | A required DLL was not found | Native dependencies and the effective DLL search path; this may appear as an immediate exit status after creation |
| Unix `ENOENT` | Program or shebang interpreter not found | Program path, `PATH`, and the script interpreter |
| Unix `EACCES` | Permission denied | Execute bit, directory traversal permission, and `noexec` mounts |
| Unix `ENOEXEC` | Executable format error | Binary format, architecture, and shebang |
| Unix `ETXTBSY` | Text file busy | Another process is writing or replacing the executable |

First call `Command.ResolveProgram()` to verify the effective child `PATH`
without spawning. Then inspect `ProcessError.Spawn.Detail`; do not retry a
permanent format or permission failure indefinitely. Program resolution and the
typed error cases are documented in
[Running commands](commands.md#program-arguments-working-directory) and
[Running commands: Errors](commands.md#errors).

## Modal Windows error dialogs from failed child processes

**Symptom:** A child that fails during startup displays a modal Windows hard-error
dialog and blocks an unattended host until somebody dismisses it.

**Cause:** ProcessKit does not pass `CREATE_DEFAULT_ERROR_MODE`, so a Windows
child inherits the process-wide error mode of its host. The default host mode can
allow Windows to display a dialog for startup failures even though the failed run
is otherwise observable through its spawn error or exit status.

**Solution:** An application that must remain unattended should call Windows
`SetErrorMode` once during host startup, before it can create any child, with
`SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX | SEM_NOOPENFILEERRORBOX`. This is
an application-level choice because the setting affects the whole process;
ProcessKit never changes it on the application's behalf. The call should be a
no-op on non-Windows platforms.

## `ProcessGroup` uses `Mechanism.ProcessGroup` instead of cgroup v2

**Symptom:** Linux reports `group.Mechanism = Mechanism.ProcessGroup` when cgroup
v2 was expected.

**Cause:** `ProcessGroup.Create()` without resource limits intentionally uses
POSIX process groups. ProcessKit selects `Mechanism.CgroupV2` only when resource
limits are requested and the real, writable cgroup v2 root is available. Ordinary
containers, nested containers, cgroup namespaces, systemd scopes, and
unprivileged users normally cannot enable controllers at that root.

**Solution:** If the outer container already enforces the required limits, use a
group without ProcessKit resource limits and accept `Mechanism.ProcessGroup`. If
ProcessKit itself must enforce limits, request them through `ProcessGroupOptions`
and provide a privileged host cgroup namespace setup. When cgroup v2 is
unavailable, a create with resource limits returns
`ProcessError.ResourceLimit`; it never silently falls back to an unenforced
limit.

See [Running in containers](containers.md#which-mechanism-you-actually-get-in-a-container)
for the privilege and nested container cases.

## `Unsupported` on a host that has PTYs

**Symptom:** `Command.Pty` or a PTY operation returns
`ProcessError.Unsupported` even though the operating system has terminal
support.

**Cause:** ProcessKit requires more than a PTY device: it must create a
controlling terminal while preserving process containment. `Unsupported` is
returned on Windows before ConPTY support, on macOS or BSD without the required
controlling terminal helper, or when Linux lacks a usable PTY device or its
`setsid --ctty` helper. That helper is deliberately loaded only from a trusted
system directory (`/usr/bin`, `/bin`, `/usr/sbin`, `/sbin`) and never from
`PATH`, so a host that keeps util-linux elsewhere reports `Unsupported` even
though `setsid` is on its `PATH` — see
[Hardening → Where the Unix helper binaries come from](hardening.md#where-the-unix-helper-binaries-come-from).
`ResizeAsync` also returns `Unsupported` for a non-PTY or
already torn down run, and session sends return it when stdin was not kept open.
In contrast, `Pty` with `Setsid`, separate stderr observation, or a nonfinal
pipeline stage is an invalid builder combination and throws `ArgumentException`.

**Solution:** Use ordinary pipes when terminal behavior is unnecessary. For a
real interactive child, verify the platform prerequisites, keep the PTY run
alive until all interaction and resize calls finish, and remove incompatible
builder options. The supported combinations and containment caveats are in the
[PTY guide](pty.md#platform-support).

## Antivirus or EDR interference during spawn

**Symptom:** Process creation is intermittently slow, hangs before user code
runs, returns access or sharing failures, or the child disappears immediately.

**Cause:** Antivirus and endpoint detection and response (EDR) tools can scan,
quarantine, inject into, suspend, or block a new process. This can look like a
ProcessKit timeout or spawn defect even when program resolution and arguments
are correct.

**Solution:** Record the program path, elapsed time, and complete typed error
without logging secret arguments or environment values. Use `ResolveProgram()`,
then reproduce with a small, trusted local executable from a normal directory.
Check the security product's event log, quarantine history, and the Windows
event log at the same timestamp. Compare with a clean host or an approved policy
allowlist; ask the security administrator for a narrow diagnostic exception
rather than disabling protection.

Keep a `Timeout` around the run and avoid unlimited retries, which can amplify a
security product's intervention. See [Running commands](commands.md#errors) for
error classification and [Hardening untrusted children](hardening.md) for safe
diagnostic boundaries.

## "No test is available" in `ProcessKit.Testing` consumers

**Symptom:** The F# test project builds, but `dotnet test` reports
`No test is available` and none of the tests using `ProcessKit.Testing` run.

**Cause:** An F# module compiles to a static class. NUnit skips that shape during
test discovery, so module functions marked with `[<Test>]` can compile without
becoming discoverable tests. This is an NUnit fixture shape issue, not a
`ProcessKit.Testing` runner or fake process failure.

**Solution:** Put tests in a `[<TestFixture>]` type and use instance
`[<Test>]` members:

```fsharp
open NUnit.Framework

[<TestFixture>]
type ProcessTests() =

    [<Test>]
    member _.``wrapper handles a successful process``() =
        // Arrange the ProcessKit.Testing runner and invoke the wrapper here.
        Assert.Pass()
```

Confirm that the test project also references its normal NUnit adapter and
`Microsoft.NET.Test.Sdk`. The working fixture pattern and ProcessKit doubles are
shown in the [testing guide](testing.md#scripting-replies).

---
