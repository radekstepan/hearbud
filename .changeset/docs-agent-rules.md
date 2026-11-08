---
"hearbud": patch
---

Docs + agent-rules hardening

- `docs/WINDOWS-RUNNER.md` (new): self-hosted runner inventory — labels,
  45 min timeout, pre-setup vs setup-dotnet SDK flow, RUNNER_TOOL_CACHE
  writability, 5 GB disk gate, NETWORK SERVICE constraints, re-provision
  via `scripts/ensure-deps.ps1 -Check`, service-env-restart gotchas.
  Unknown host facts marked TODO, nothing invented.
- `docs/CONTRIBUTING.md` (append-only): `Logging Discipline` under
  Debugging (transitions-only, no per-frame, WARN/ERROR + tail cap,
  1 Hz backlog/lag with thread/tick context) and `Changesets` under
  Development Workflow (one `.changeset/<kebab>.md` per commit,
  `"hearbud": patch`, What/Why/Verification, consumed on merge).
- `AGENTS.md` (new, root): binding subagent rules — verify-by-execution,
  file-partition discipline, minimal edits, changeset-per-commit,
  evidence-before-synthesis, short factual communication.
- `.github/workflows/commitlint.yml` (new): conventional-commit subject
  gate on ubuntu-latest (bash + git log, checkout@v4 only), exempting
  `[skip ci]` and master version-bumps.
- What: docs + workflow YAML only, no production code changes.
- Why: runner knowledge was YAML-only; logging/changeset/commit rules
  were tribal. Locks them in for future subagent work.
- Verification: docs + workflow YAML only; YAML not run locally — CI gate.
