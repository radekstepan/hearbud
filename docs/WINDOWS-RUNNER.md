# Windows Runner (self-hosted)

Host inventory for the CI build runner. Source of truth for labels,
timeouts, and preflight gates is `.github/workflows/build.yml`; this doc
explains what that workflow assumes about the machine.

## Labels and scheduling

- Runs on `[self-hosted, Windows, X64]` (`build-windows` job in `build.yml`).
- `changeset-check` runs on `ubuntu-latest` so PRs get fast feedback even
  when this runner is busy or offline.
- `timeout-minutes: 45` on `build-windows` — a hung restore/test/publish
  fails instead of blocking the queue.
- One more serialization note: `build.yml` sets `concurrency:
  <workflow>-<ref>` with `cancel-in-progress: false` so concurrent master
  pushes cannot race the version-bump push.

## Runner identity

- TODO: hostname — fill in (do not guess; run `hostname` on the host).
- TODO: IP address — fill in only if a static IP/DNS entry is provisioned.
- TODO: OS build (run `winver` / `[Environment]::OSVersion` on the host).
- Service account: NETWORK SERVICE (see constraints below).
- TODO: runner version (`./run.sh --version` / service properties).

## .NET SDK flow: pre-setup vs setup-dotnet

The workflow deliberately does NOT rely on a machine-wide SDK:

1. **Preflight** (informational only): checks `dotnet --list-sdks` for
   `8.0.1xx`. Absent or stale SDK here is a `Write-Warning`, never a
   failure — the real SDK comes next.
2. **`actions/setup-dotnet@v4`** installs SDK `8.0.x` with
   `DOTNET_INSTALL_DIR: ${{ runner.tool_cache }}\dotnet` (Program Files
   is not writable as NETWORK SERVICE).
3. **Verify .NET** step fails the job if `dotnet` is still missing after
   setup-dotnet.

Local parity: `scripts/ensure-deps.ps1 -Check` verifies a machine-wide
`dotnet 8.0.1xx` because a dev box installs the SDK normally; CI does
not require that.

## Preflight gates (fail the job)

From the `Preflight` step in `build.yml` (mirrored read-only by
`scripts/ensure-deps.ps1 -Check`):

| Gate | Check | Why |
|------|-------|-----|
| git | `Get-Command git` | Checkout + version-bump push need it; install Git for Windows machine-wide |
| VC++ runtime | `Test-Path $env:SystemRoot\System32\vcruntime140.dll` | Native deps fail without the redist |
| Network | DNS resolve `api.nuget.org` | Restore cannot work offline |
| Disk | `>= 5GB` free on `C:` | Restore + publish + self-contained output need headroom |
| `RUNNER_TOOL_CACHE` | env var set, dir exists, file-write probe | setup-dotnet install target; must be writable by the service account |

## NETWORK SERVICE constraints

- The runner service runs as NETWORK SERVICE: **no installs from CI**.
  Nothing in `build.yml` installs git, the VC++ redist, or a
  machine-wide SDK — those are host prerequisites.
- There is no sudo/UAC-elevation equivalent from a job step. If a
  preflight gate fails on a missing machine-wide prerequisite, fix it
  on the host (admin session), not in YAML.
- `RUNNER_TOOL_CACHE` ACLs must grant the service account write access;
  the preflight write probe (`WriteAllText` + delete of
  `.hearbud-write-probe`) catches this early.

## Re-provisioning

Read-only check (safe as NETWORK SERVICE — no installs, no writes):

```powershell
pwsh -File scripts/ensure-deps.ps1 -Check
```

Install links (run interactively as admin on the host):

- Git for Windows (machine-wide): <https://git-scm.com/download/win>
- .NET 8 SDK (8.0.1xx): <https://dotnet.microsoft.com/download/dotnet/8.0>
- VC++ Redistributable: <https://learn.microsoft.com/cpp/windows/latest-supported-vc-redist>

After installing host prerequisites, re-run with `-Check` until it
prints the `ensure-deps -Check passed` line.

## Gotchas

- **Service env restart:** after installing the .NET SDK or git
  machine-wide, restart the runner service (or the host) so the service
  environment picks up the new `PATH`. Preflight may otherwise report
  `dotnet not on PATH` / missing git until the restart.
- **Machine-wide git / VC++ redist required:** per-user installs are
  invisible to NETWORK SERVICE. Install both with machine-wide scope.
- **Tool-cache survives jobs:** setup-dotnet caches under
  `RUNNER_TOOL_CACHE`; a stale SDK there is reused. If SDK behavior
  looks pinned, inspect `$env:RUNNER_TOOL_CACHE\dotnet` on the host.
- **Disk gate is on `C:` only:** free space on other volumes does not
  count — restore, test output, and `dist/` all land on `C:`.
- TODO: anything host-specific discovered during the next re-provision
  (AV exclusions, proxy env vars, power policy) goes here.
