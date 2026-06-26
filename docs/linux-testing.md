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
