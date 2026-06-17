#!/usr/bin/env bash
#
# Checks this machine can build and test an F# (.NET) project before you
# initialize the template (POSIX counterpart of check-env.ps1 — use whichever
# matches your shell; both do the same thing).
#
# Verifies the .NET SDK is installed and new enough (the major band pinned in
# global.json). Exits 0 when ready; if a required tool is missing it prints
# per-OS install commands and exits 1 — install what it names, then re-run.
# (Fantomas is a local tool restored by `dotnet tool restore`, not a separate
# environment prerequisite, so it is not checked here.)
#
# Usage: bash ./scripts/check-env.sh

set -euo pipefail
case "${1:-}" in -h|--help) sed -n '2,13p' "$0"; exit 0 ;; esac

script_dir="$(cd "$(dirname "$0")" && pwd)"

# Required .NET major version — read from global.json when present, else default.
required_major=10
global_json="$script_dir/../global.json"
if [ -f "$global_json" ]; then
  v="$(sed -n 's/.*"version"[[:space:]]*:[[:space:]]*"\([0-9]\{1,\}\)\..*/\1/p' "$global_json" | head -n1 || true)"
  [ -n "$v" ] && required_major="$v"
fi

problems=()
echo "==> Checking environment for F# (.NET) development"

# Required: the .NET SDK (it bundles the F# compiler and `dotnet test`).
if ! command -v dotnet >/dev/null 2>&1; then
  problems+=("the .NET SDK ('dotnet' is not on PATH)")
elif dotnet --list-sdks | awk -F. -v m="$required_major" '($1+0)>=m{f=1} END{exit f?0:1}'; then
  echo "    .NET SDK ${required_major}+ found"
else
  problems+=("a .NET ${required_major} SDK (dotnet found, but no installed SDK >= ${required_major})")
fi

# Soft: git drives the init defaults (author/email) and the VCS workflow.
command -v git >/dev/null 2>&1 || \
  echo "    note: git is not on PATH — init falls back to placeholder author/email."

if [ ${#problems[@]} -eq 0 ]; then
  echo
  echo "Environment ready. Next: bash ./scripts/init.sh --project-name ..."
  exit 0
fi

echo
echo "Environment NOT ready. Missing:"
for p in "${problems[@]}"; do echo "  - $p"; done
echo
echo "Install the .NET ${required_major} SDK, then re-run this check:"
echo "  Windows : winget install Microsoft.DotNet.SDK.${required_major}"
echo "  macOS   : brew install --cask dotnet-sdk"
echo "  Linux   : see https://learn.microsoft.com/dotnet/core/install/linux"
exit 1
