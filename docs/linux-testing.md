# Running tests on Linux from Windows

The Linux/Unix code path is exercised in CI on `ubuntu-latest`, but you can
also run the suite locally against a Linux container — useful when changing
platform-specific code.

> Optional helper. Delete this file and `scripts/test-linux.ps1` if your
> project does not need Linux testing from Windows.

## Requirements

- [Rancher Desktop](https://rancherdesktop.io/) (or Docker Desktop) with the
  `dockerd` / moby engine enabled so `docker` is on `PATH`
- PowerShell 7+

## Usage

```pwsh
pwsh ./scripts/test-linux.ps1
```

The script mounts the repo into `mcr.microsoft.com/dotnet/sdk:10.0` and runs
`dotnet build` + `dotnet test` for the `net10.0` target framework (that image
carries only the net10 runtime; CI exercises both `net8.0` and `net10.0`). The host's `bin/` and `obj/` folders are
shadowed inside the container with anonymous volumes, so the Linux build
neither sees the Windows IDE artifacts nor writes back into the host tree.
A named volume (`ProcessKit-nuget`) caches NuGet packages between runs.

Useful switches:

```pwsh
pwsh ./scripts/test-linux.ps1 -Filter "FullyQualifiedName~greet"
pwsh ./scripts/test-linux.ps1 -Configuration Debug -Rebuild
```

## musl/Alpine

Alpine (musl libc) is the de facto standard base for containerized .NET
deployments and differs from the default glibc image in libc behaviour and in
which utilities ship in the base image. Pass `-Alpine` to run the same suite
against `mcr.microsoft.com/dotnet/sdk:10.0-alpine` instead:

```pwsh
pwsh ./scripts/test-linux.ps1 -Alpine
pwsh ./scripts/test-linux.ps1 -Alpine -Filter "FullyQualifiedName~VerbTests"
```

`-Alpine` changes two things under the hood, both transparent to normal use:

- The in-container script runs under `sh` instead of `bash` — Alpine's base
  image has no bash, only BusyBox's `sh` (every line the script runs is plain
  POSIX shell, so this is a no-op for the default image).
- `apk add --no-cache util-linux` runs before the build. BusyBox ships its own
  `setpriv` applet under the same name, but it does not implement the
  `--reuid`/`--regid`/`--clear-groups` flags the `Command.Uid`/`Gid`
  privilege-drop path (`Native.Posix.fs`) rewrites onto — without the real
  util-linux package shadowing that applet, the privilege-drop tests fail with
  a `setpriv: unrecognized option` spawn error rather than exercising the real
  drop.

No other accommodation was needed: the rest of the suite (build, streaming,
readiness probes, pipelines, signals, `/proc`-based introspection) runs
unmodified against musl — it was confirmed green end-to-end (`Category!=Stress`)
against this exact image before the CI leg below was added.

CI runs the same combination in the `test-alpine` job
([CI workflow](https://github.com/ZelAnton/ProcessKit-fSharp/blob/main/.github/workflows/ci.yml)), by the same raw
`docker run` pattern as the `test-cgroup-limits` job (rather than the OS matrix
in `test`, since it needs the `util-linux` install step first): full suite,
`Category!=Stress`, `net10.0` only (the `-alpine` image, like the default
image, ships only the net10 runtime).
