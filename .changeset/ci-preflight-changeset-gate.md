---
"hearbud": patch
---

CI hardening (ere-core preflight/bootstrap parity)

What:
- `build.yml` Preflight extended (existing git/vcruntime140/api.nuget.org
  checks kept): informational dotnet SDK 8.0.1xx check that warns instead of
  failing pre-setup-dotnet, fail if C: free < 5GB via `Get-PSDrive`, and a
  `$env:RUNNER_TOOL_CACHE` writable probe. Actions noted as pinned to major
  versions (v4) mirroring ere-core.
- New `changeset-check` job on `ubuntu-latest` (no self-hosted dependency):
  fails non-master pushes lacking `[skip ci]` when no `.changeset/*.md`
  (excluding `README.md`) is present.
- New `scripts/ensure-deps.ps1`: `-Check` read-only verify (git, dotnet
  8.0.x via `dotnet --list-sdks`, vcruntime140.dll, api.nuget.org DNS, 5GB
  free) that throws on failure; without `-Check` prints install guidance
  (dotnet 8 SDK + VC++ redist links). No installs, no writes — safe as
  NETWORK SERVICE.

Why: fail fast on runner misconfiguration, enforce changesets on branches,
and give a local bootstrap parity script matching ere-core.

Verification: YAML/ps1 syntax not executed locally — needs a CI run on the
Windows runner. Reviewer: please confirm the Preflight and changeset-check
jobs are green.
