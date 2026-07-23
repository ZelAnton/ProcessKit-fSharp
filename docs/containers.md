# Running in containers

[Previous: Overview](./)

Containers are the default deployment target for server .NET, and they are also where the
platform fine print in [Platform support](platform-support.md) matters most: which containment
`Mechanism` a `ProcessGroup` actually gets, whether it behaves as `PID 1`, and whether its base
image even has a shell all depend on the container, not on ProcessKit. This guide collects the
container-specific consequences of that fine print in one place — it does not repeat the
mechanism/capability details already covered in [Platform support](platform-support.md), it builds
on them.

- [Which mechanism you actually get in a container](#which-mechanism-you-actually-get-in-a-container)
- [Running as PID 1](#running-as-pid-1)
- [Graceful shutdown on orchestrator SIGTERM](#graceful-shutdown-on-orchestrator-sigterm)
- [Minimal images: musl/Alpine and shell-less images](#minimal-images-muslalpine-and-shell-less-images)
- [Container resource limits vs `ProcessGroupOptions` limits](#container-resource-limits-vs-processgroupoptions-limits)

## Which mechanism you actually get in a container

On Linux, `ProcessGroup.Create()` picks `Mechanism.ProcessGroup` (POSIX process groups) unless you
ask for resource limits, in which case it needs a **real, writable cgroup v2 hierarchy** to grant
`Mechanism.CgroupV2` — see
[cgroup v2 needs the real cgroup root](platform-support.md#caveats) in Platform support. That
matters most inside a container, because the cgroup filesystem an ordinary container sees is a
private **cgroup namespace**, not the host's real root, and cgroup v2's "no internal processes"
rule only lets you enable the controllers a limit needs (writing `cgroup.subtree_control`) at the
real root:

- **An ordinary, unprivileged container or Kubernetes pod** does not expose the real cgroup v2
  root. A limit-free `ProcessGroup.Create()` still works fine there (you get
  `Mechanism.ProcessGroup`), but `ProcessGroup.Create(optionsWithLimits)` fails fast with
  `ProcessError.ResourceLimit` — **not** a silent fallback to the process-group mechanism. An
  unenforced cap is not a real cap, so ProcessKit refuses to hand back a group that looks limited
  but isn't.
- **A privileged container run with the host's cgroup namespace** (`docker run --privileged
  --cgroupns=host`, or an equivalent host-cgroup-namespace setup) does expose the real root, so
  requesting limits there succeeds and grants `Mechanism.CgroupV2`. This is exactly the shape this
  repository's own CI uses to exercise the cgroup v2 backend for real (the `test-cgroup-limits` job
  in [`.github/workflows/ci.yml`](../.github/workflows/ci.yml) runs `docker run --rm --privileged
  --cgroupns=host …`); ordinary CI containers (and ordinary production containers) do not have this
  and are not expected to.

The practical rule: **don't request `ProcessGroupOptions` limits from inside an ordinary
container** unless you know it was started with host-cgroup-namespace privileges — check
`ProcessGroup.Mechanism` (or handle `ProcessError.ResourceLimit` from `Create`) rather than
assuming. If the container itself already has resource caps (the usual case — see
[Container resource limits vs `ProcessGroupOptions` limits](#container-resource-limits-vs-processgroupoptions-limits)
below), you may not need group-level limits at all.

**F#**

```fsharp
let options =
    ProcessGroupOptions()
        .WithMemoryMax(256L * 1024L * 1024L)

match ProcessGroup.Create options with
| Ok group ->
    use group = group
    printfn $"got {group.Mechanism} with limits actually enforced"
| Error(ProcessError.ResourceLimit msg) ->
    // typical inside an ordinary, unprivileged container: no real cgroup v2 root to enable
    // the memory controller on, so the cap can't be enforced — fail loudly instead of running
    // unbounded, then fall back to relying on the container's own memory limit instead.
    eprintfn $"container has no usable cgroup v2 root: {msg}"
| Error err -> eprintfn $"{err.Message}"
```

**C#**

```csharp
var options = new ProcessGroupOptions().WithMemoryMax(256L * 1024L * 1024L);

var created = ProcessGroup.Create(options);
switch (created)
{
    case { IsOk: true, ResultValue: var group }:
        using (group)
            Console.WriteLine($"got {group.Mechanism} with limits actually enforced");
        break;
    case { IsOk: false, ErrorValue: ProcessError.ResourceLimit { Detail: var msg } }:
        // typical inside an ordinary, unprivileged container — see the F# comment above.
        Console.Error.WriteLine($"container has no usable cgroup v2 root: {msg}");
        break;
    case { IsOk: false, ErrorValue: var err }:
        Console.Error.WriteLine(err.Message);
        break;
}
```

## Running as PID 1

A containerized app is commonly `PID 1` inside its PID namespace (no init process ahead of it),
which brings the two well-known Unix `PID 1` responsibilities: the kernel reparents **any**
orphaned descendant in the namespace to `PID 1`, and `PID 1` gets no default disposition for
signals it hasn't explicitly handled (an unhandled `SIGTERM` sent to `PID 1` is ignored by the
kernel, unlike for any other process).

What this means for a ProcessKit-using app:

- **Zombie reaping for ProcessKit's own tree is already covered, PID 1 or not.** Whatever spawned a
  process — `Command`, a `ProcessGroup`, a `Supervisor` — is reaped by ProcessKit's own POSIX
  backend: a shared `SIGCHLD`-driven `waitpid` (or the Linux `pidfd` fast path) reaps every process
  it tracks the moment it exits, regardless of where in the process tree it ends up. This is not a
  `PID 1`-specific behavior — it is how the library always avoids leaving zombies behind for the
  processes it spawned.
- **Reparenting does not let a process escape `Mechanism.JobObject` / `Mechanism.CgroupV2`
  containment**, because those two mechanisms track membership by kernel container (the Job / the
  cgroup), not by parent-child ancestry — reparenting a grandchild to `PID 1` doesn't remove it from
  its Job or cgroup. The one mechanism with a real escape hatch is `Mechanism.ProcessGroup`, and only
  via a deliberate `setsid()` inside the child — see
  [POSIX process groups: a `setsid` child can escape](platform-support.md#caveats) in Platform
  support, which applies identically whether or not you're `PID 1`.
- **Orphans outside ProcessKit's own tracking are not ProcessKit's concern.** If something else in
  the same container — a shell script, another library, a debugging tool you exec'd manually —
  spawns processes that ProcessKit never tracked, those still reparent to your app as `PID 1` when
  their own parent exits, and *something* has to `wait()` on them or they sit as zombies until the
  container's `PID 1` exits. ProcessKit only reaps what it spawned or was asked to track (a
  `ProcessGroup`'s members); it is not a general-purpose subreaper for the whole PID namespace. If
  your container only ever runs processes through ProcessKit, this does not come up. If it also runs
  ad hoc child processes outside ProcessKit, put a minimal init ([`tini`](https://github.com/krallin/tini)
  or your container runtime's built-in equivalent — Docker's `--init` flag, Kubernetes' `shareProcessNamespace`
  is unrelated) ahead of your app as the real `PID 1`, so it reaps those and forwards signals down to
  your (now `PID 2`) app.
- **Signal delivery to your app.** The `PID 1`-ignores-unhandled-signals rule is about the *kernel's
  default disposition* being skipped — it does not apply once something in your process installs a
  handler for that signal. See
  [Graceful shutdown on orchestrator SIGTERM](#graceful-shutdown-on-orchestrator-sigterm) below for
  how to make sure your app actually reacts to the orchestrator's `SIGTERM` rather than silently
  ignoring it as `PID 1`.

## Graceful shutdown on orchestrator SIGTERM

Docker and Kubernetes stop a container by sending `SIGTERM` to its `PID 1`, waiting up to a grace
period (Kubernetes' `terminationGracePeriodSeconds`, default 30s), then `SIGKILL`ing anything still
alive. Wiring that into `ProcessGroup.ShutdownAsync` — [`SIGTERM` → grace window → `SIGKILL`
survivors on the Unix mechanisms](process-groups.md#tearing-down-dispose-terminate-shutdown), the
atomic Job terminate on Windows — gives your contained tree the same two-phase shutdown the
orchestrator itself expects, instead of a hard kill on every stop:

**F#**

```fsharp
open System

let run (group: ProcessGroup) (appLifetimeToken: CancellationToken) =
    task {
        use _ =
            appLifetimeToken.Register(fun () ->
                // React to the orchestrator's SIGTERM (surfaced through your app's own signal /
                // host-lifetime plumbing — see the .NET Generic Host note below) by giving the tree
                // a grace window before the orchestrator's own SIGKILL would land.
                group.ShutdownAsync(TimeSpan.FromSeconds 10.0) |> ignore)

        match! group.StartAsync(Command.create "worker") with
        | Ok _worker -> ()
        | Error err -> eprintfn $"{err.Message}"
    }
```

**C#**

```csharp
appLifetimeToken.Register(() =>
{
    // See the F# comment above.
    _ = group.ShutdownAsync(TimeSpan.FromSeconds(10));
});

await group.StartAsync(new Command("worker"));
```

If your app is built on the **.NET Generic Host** (`Microsoft.Extensions.Hosting`), you don't have
to wire the `SIGTERM` handling yourself: the host already translates the orchestrator's `SIGTERM`
into `IHostApplicationLifetime.ApplicationStopping`, and the
[`ProcessKit.Extensions.Hosting`](dependency-injection.md#hosting-a-supervised-child) package's
hosted process already calls `RunningProcess.StopAsync` during host shutdown, configurable per
registration with `ConfigureProcessKitHostedProcess(name, o => o.ShutdownGracePeriod = …)`. Whichever
path you use, keep the grace window (`ShutdownAsync`'s argument, or `ShutdownGracePeriod`, or
`ProcessGroupOptions.WithShutdownTimeout`) **comfortably shorter** than the orchestrator's own grace
period (Kubernetes' `terminationGracePeriodSeconds`, Docker's `--stop-timeout` / `stop_grace_period`
in Compose) — if your own grace window doesn't finish first, the orchestrator's `SIGKILL` reaches
your `PID 1` (and, on Linux/macOS mechanisms, the whole tree with it) before `ShutdownAsync` gets to
run its own escalation, which is a much blunter stop than the one this library is trying to give
you.

## When the parent is killed outright: `Command.KillOnParentDeath`

The graceful path above assumes your app *gets* to run its shutdown. It might not: if your own grace
window overruns, the orchestrator escalates to `SIGKILL`, and a `SIGKILL`ed parent runs no
`Dispose`/finalizer — so `ProcessGroup` teardown never fires. On the **Windows** Job Object and
**Linux cgroup v2** mechanisms the container still reaps the tree (kernel-enforced membership, not
parent bookkeeping), but on the **POSIX process-group** mechanism a hard-killed parent can leave its
children running, reparented to `PID 1`.

`Command.KillOnParentDeath()` opts a child in to being reaped when its parent dies *suddenly*. It is a
best-effort backstop, not a replacement for `ShutdownAsync`, and the guarantee is platform-specific —
`Command.KillOnParentDeathScope()` reports the honest scope (fixed per platform, whether or not the
verb was set):

- **Windows** — the whole tree, already, with no opt-in (the Job Object's `KILL_ON_JOB_CLOSE` fires
  when the kernel closes the dead parent's last Job handle during process rundown).
- **Linux** — the **direct child only**, via `PR_SET_PDEATHSIG(SIGKILL)` armed through the
  `setpriv --pdeathsig` helper. A **grandchild** is not covered (the signal is not inherited across a
  `fork`), and the kernel resets it across an `execve` of a **set-uid/set-gid** image.
- **macOS/BSD** — no `PR_SET_PDEATHSIG` analog; a set value fails the spawn with
  `ProcessError.Unsupported`, never a silent no-op.

See [platform-support.md](platform-support.md#caveats) for the full caveats.

## Minimal images: musl/Alpine and shell-less images

ProcessKit's baseline path needs neither a shell nor any extra binary: spawning, capturing,
streaming, timeouts, pipelines, and POSIX-process-group / Job Object containment are all direct
`posix_spawn(3)` / Win32 calls. A few **opt-in** Unix features are the exception, and each needs a
specific external helper:

- **`Command.Uid` / `Command.Gid` (privilege dropping)** and **`Command.KillOnParentDeath`** both
  rewrite the spawn to run through [`setpriv`](https://man7.org/linux/man-pages/man1/setpriv.1.html)
  (util-linux): a `Uid`/`Gid` drop because `posix_spawn` has no uid/gid attribute of its own, and
  `KillOnParentDeath` because `PR_SET_PDEATHSIG` must be armed by a process that then `exec`s the
  target in place (`setpriv --pdeathsig`) rather than by managed .NET code in an unsafe forked child.
  `setpriv` ships on mainstream glibc-based Linux (Debian/Ubuntu, the distributions ProcessKit's own
  CI runs on) but is **commonly absent from a minimal musl image** (a bare Alpine base, or
  `FROM scratch` / distroless-style images) — where it's missing, the spawn fails with a typed
  `ProcessError.Spawn` naming the missing helper, never a silent unprivileged / un-armed run. If your
  image needs `Uid`/`Gid` dropping or `KillOnParentDeath`, install `util-linux` (`apk add util-linux`
  on Alpine) — or, for privilege dropping, drop another way (a distroless multi-stage image copying
  only the published output as a non-root `USER`, so the *container* never runs as root in the first
  place and `Uid`/`Gid` is unnecessary).
- **`ProcessGroupOptions` resource limits on Linux** are enforced through a private cgroup v2 whose
  self-migrating launcher is [a tiny `/bin/sh` script](platform-support.md#containment-mechanisms)
  that joins the cgroup and then `exec`s the real target in place. A shell-less image (no
  `/bin/sh` at all) makes that launcher unavailable, and `ProcessGroup.Create` with limits
  requested fails with `ProcessError.ResourceLimit` naming `/bin/sh` as the missing piece — the
  same honest-failure contract as the missing real cgroup root case above. Ordinary spawning
  (no limits requested) needs no shell at all, so a shell-less final stage is otherwise fine.

A representative multi-stage Dockerfile for a `net10.0` console app that uses ProcessKit, ending on
a musl (Alpine) runtime image:

```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish MyApp/MyApp.fsproj -c Release -o /app

# musl-based runtime image. Add util-linux only if the app calls Command.Uid/Gid or KillOnParentDeath.
FROM mcr.microsoft.com/dotnet/runtime:10.0-alpine AS final
# RUN apk add --no-cache util-linux   # only needed for Command.Uid / Command.Gid / KillOnParentDeath
WORKDIR /app
COPY --from=build /app .
USER 10001:10001
ENTRYPOINT ["dotnet", "MyApp.dll"]
```

Running as a non-root `USER` in the image (as above) is generally the better fit for a minimal
image than asking ProcessKit to drop privileges at spawn time with `Uid`/`Gid` — it needs no extra
package and applies to the whole container, not just processes ProcessKit spawns.

## Container resource limits vs `ProcessGroupOptions` limits

These are two different layers, and they don't require each other:

- **The container's own limits** — Docker's `--memory` / `--cpus`, Kubernetes'
  `resources.limits`, the underlying cgroup the *container runtime* set up around the whole
  container — cap everything inside the container, including your app and every process
  ProcessKit ever spawns. These are always in effect (that's what a container is), independent of
  anything ProcessKit does, and they're usually the right place for an overall ceiling on the
  workload.
- **`ProcessGroupOptions`' `WithMemoryMax` / `WithMaxProcesses` / `WithCpuQuota`** (see
  [Resource limits](process-groups.md#resource-limits) in Process groups) cap a *specific* group of
  processes you spawn — narrower than the container, and enforced (on Linux) by a **nested** cgroup
  v2 inside the container's own cgroup. As covered above, that nesting needs the real cgroup v2
  root, which an ordinary container doesn't expose — so group-level limits are realistically an
  opt-in for containers deliberately set up to expose it (privileged + host cgroup namespace, as
  ProcessKit's own `test-cgroup-limits` CI job does), not something to reach for by default inside
  an arbitrary production container.

In most containerized deployments, the container's own memory/CPU limit is already the ceiling that
matters, and `ProcessGroupOptions` limits are for the narrower case of bounding one spawned tool
*within* a container's broader budget — a build step's compiler, an untrusted subprocess, a fork
bomb guard on a supervised worker — where the container-level cap alone can't distinguish between
that one process and the rest of the workload sharing the container.
