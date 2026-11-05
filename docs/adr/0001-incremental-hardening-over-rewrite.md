# ADR-0001: Incremental hardening on CSCore / .NET 8 (defer the NAudio 3 rewrite)

> **Superseded by [ADR-0012](0012-phase4-naudio-rewrite.md).** The deferred
> NAudio rewrite landed as Phase 4; the incremental history (Phases 0–3)
> below stands as record. Links to `docs/v2/` are historical — that folder
> was removed after the migration landed; lasting content lives in
> `../TROUBLESHOOTING.md` and `../CONTRIBUTING.md`.

Status: superseded by ADR-0012

## Context

The sibling `hearbud-b` blueprint ("Hearbud v2 — Bulletproof Rewrite") diagnoses
real fragilities in this app and proposes a from-scratch rewrite on NAudio 3 /
.NET 9: `WasapiRecorderBuilder`, timestamp sync with a drift PLL, RF64 writers,
and a headless engine library.

A flag-day rewrite has real costs for this repo right now:

- The current CSCore pipeline is well-understood, tested (xUnit), and shippable;
  a parallel rewrite doubles the code to maintain until cutover.
- NAudio 3 targets `net9.0-windows`, forcing a framework retarget plus
  re-validation of every device/format path on real hardware.
- Several blueprint wins don't need NAudio at all (non-blocking handoff,
  tmp→atomic-rename, preflight checks, health supervision, backoff).

## Decision

Port the blueprint **incrementally** on the existing .NET 8 + CSCore stack:

1. Land every hardening idea that fits the current pipeline (this change).
2. Stage pure, tested DSP building blocks (hum detector, kneed limiter) without
   wiring them into the hot path.
3. Defer the NAudio 3 capture swap, drift PLL, RF64 header re-patch, per-leg
   auto-rebuild, and out-of-process MP3 to later phases (see
   [docs/v2/MIGRATION_PLAN.md](../v2/MIGRATION_PLAN.md)).

## Consequences

- Good: each change is small, reviewable, and covered by the existing xUnit
  suite; v1 keeps shipping with zero behavior regressions.
- Good: QPC timestamps and health counters landed now become the measurement
  foundation the later PLL/rebuild work needs.
- Bad: CSCore stays (unmaintained since ~2018). The rewrite question is deferred,
  not answered — revisit when capture-level features (raw mode, per-process
  loopback, IAudioClient3) are required.
