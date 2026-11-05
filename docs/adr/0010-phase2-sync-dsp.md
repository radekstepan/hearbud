# ADR-0010: Phase 2 — timestamped mic sync (PLL), kneed limiter, DC blocking

Status: accepted (supersedes the "limiter staged" half of ADR-0007)

## Context

Phase 2 is the first sample-path change: chunk-count sync + the stale-backlog
guillotine + global tanh are exactly what the blueprint blames for creep,
clicks, and dull transients. Wiring replacements blind (no audio hardware on
this machine) is only defensible if the new pieces are pure, heavily tested,
and keep every existing observable (counters, files, events) intact.

## Decision

1. **`TimestampedMicBuffer` replaces the ring.** Mic blocks are written with
   arrival-QPC stamps; each loopback block reads from (loopNow − 120 ms).
   A 1 s timestamp horizon drops stale mic (no mode branches, no guillotine);
   a PLL servos depth toward 120 ms by bending the read rate in
   [0.999, 1.001] (blueprint numbers: α=0.002, gain=2e-5); a fractional cursor
   applies the ratio via linear interpolation with no per-block rounding
   jitter. Overflow past 30 s drops oldest and counts it.
2. **Inner interpolator stays linear for now.** Wdl-quality resampling needs
   NAudio (net9); Phase 4 swaps the inner lerp while the ratio servo,
   horizon, and cursor stay byte-identical (the blueprint's own endorsed
   simplification).
3. **Global tanh → `SoftLimiter`.** Deliberate contract change: the mix is now
   transparent below the 0.8 knee. The `SoftClip*` tests were removed and
   replaced by pointer comments; `SoftLimiterTests` carry the new contract.
4. **DC blocking on mix legs only** (`DcBlocker`, ~8 Hz 1-pole HPF). Raw
   sys/mic recordings stay bit-raw; three instances (sys, mic-in-mix,
   mic-direct) keep threads separate with no locks.
5. **Observables preserved:** `_micUnderrunBlocks` (any zero-fill),
   backlog seconds (now `DepthSec`), the 50-block backlog log line, file set,
   and all events keep their shapes; `HealthReport` is unchanged.

## Consequences

- Good: no more creep/guillotine/underrun-click class; every mechanism is
  unit-tested (16 buffer tests incl. exact-lock passthrough, horizon edges,
  PLL clamp directions, overflow; 7 DC tests).
- Good: 198/198 green (removed 9 superseded tests, added 23).
- Bad: linear interp still aliases ultrasonics until Phase 4's Wdl swap.
- Bad: no listening test was possible here — the Windows gate (8-hour soak,
  drift < ±2 ms, blind A/B vs Phase 1) is mandatory before calling this done.
