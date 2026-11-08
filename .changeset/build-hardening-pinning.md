---
"hearbud": patch
---

Build hardening: warnings-as-errors, analyzers, NuGet audit, and SDK pinning.

## What
- `Directory.Build.props`: added central hardening flags (`TreatWarningsAsErrors`, `EnforceCodeStyleInBuild`, `AnalysisMode=Recommended`, `EnableNETAnalyzers`, `NuGetAudit`, `NuGetAuditMode=all`) while keeping `<Version>` as the single source of truth.
- `global.json`: changed `rollForward` from `latestFeature` to `latestPatch`, keeping SDK `8.0.100` pinned to the feature band while still allowing security patches.
- Added minimal `.editorconfig` (UTF-8, LF, 4-space C# indent, trim whitespace, final newline).
- Added `.github/dependabot.yml` (weekly `nuget` + `github-actions` updates).

## Why
Parity with ere-core build hardening: fail the build on warnings, enforce code style and recommended analyzers in build, audit all NuGet vulnerabilities, pin the SDK feature band, and keep dependencies fresh via Dependabot. Lockfile generation intentionally skipped (no local dotnet toolchain to generate it).

## Verification
No local dotnet toolchain on this Linux box, so no local build was run. Verification is CI `dotnet test` + `dotnet publish` on the Windows runner.
