# ADR-0007: Stage DSP building blocks tested but unwired (hum watch, kneed limiter)

Status: accepted

## Context

The blueprint prescribes three DSP changes: a Goertzel-based 50/60 Hz hum
watchdog, a DC blocker, and replacing global `tanh` with a kneed soft-limiter
(tanh dulls even quiet transients). All three sit in the sample path, so wiring
them wrong changes what users hear — and this pass deliberately makes zero
sample-path changes (ADR-0001).

## Decision

Land `Goertzel`/`HumWatch` and `SoftLimiter` as pure, unit-tested classes that
are **not yet called** from the audio callbacks:

- `Goertzel`: single-frequency detector; `HumWatch`: 50/60/100/120 Hz bank with
  a −50 dB detect threshold. Tests synthesize exact-bin tones (silence floor,
  on/off separation > 20 dB, 50 vs 60 Hz identification, music-tone rejection).
- `SoftLimiter`: transparent below the 0.8 knee, smoothly ceilinged to ±1, with
  tanh-compatible ±∞/NaN semantics. Tests cover passthrough, odd symmetry,
  continuity, monotonicity, and the ceiling.
- DC blocker: intentionally omitted — trivial to add, but pointless without a
  mixer stage to live in; it ships with Phase 2.

Wiring plan (migration Phase 2): feed `HumWatch` from the mixer thread (or a 1 s
snapshot analyzed on the health tick) and surface the result in `HealthReport`;
swap `SoftClipIfNeeded` → `SoftLimiter.Limit` only after a null/listening test.

## Consequences

- Good: the riskiest DSP is designed, reviewed, and tested before it touches audio.
- Bad: dead code until Phase 2 — mitigated by tests (it can't silently rot) and
  this ADR plus the wiring plan.
