# Security Policy

## Supported versions

Security fixes are applied to the latest released version of **ProcessKit**.
Older versions are not maintained — upgrade to the latest release to receive
fixes.

## Reporting a vulnerability

**Do not open a public issue for security vulnerabilities.**

Report privately through GitHub's
[private vulnerability reporting](https://github.com/ZelAnton/ProcessKit-fSharp/security/advisories/new)
(repository **Security → Advisories → Report a vulnerability**). If that is
unavailable, contact the maintainer listed on the
[ZelAnton](https://github.com/ZelAnton) profile.

Please include:

- a description of the vulnerability and its impact;
- steps to reproduce (a minimal proof of concept is ideal);
- affected version(s).

You can expect an initial acknowledgement within a few days. Once a fix is
ready, a patched release is published to NuGet.org and the advisory is disclosed.

## Automated scanning

Dependencies are audited against the NuGet advisory database on every restore
(`NuGetAudit`/`NuGetAuditMode=all`, configured in
[`Directory.Build.props`](Directory.Build.props)), and
[Dependabot](.github/dependabot.yml) keeps GitHub Actions and NuGet packages
current.

> **No CodeQL.** GitHub CodeQL has no F# support, so this repository ships no
> CodeQL workflow. Static hygiene relies instead on `TreatWarningsAsErrors` and
> Fantomas formatting checks in CI. F# analyzers are integrated through the
> `fsharp-analyzers` local tool and the Ionide.Analyzers rule package; run
> `dotnet fsharp-analyzers --project src/ProcessKit/ProcessKit.fsproj` to check
> the main library locally.
