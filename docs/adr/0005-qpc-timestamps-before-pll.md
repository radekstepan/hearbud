# ADR-0005: Land QPC timestamps now; defer the drift PLL

Status: accepted

## Context

The blueprint's L2 principle replaces chunk-count alignment + linear resampling
with QPC-timestamped blocks and a drift-compensating resampler (PLL ratio
0.999–1.001), because independent mic/loopback crystals drift ~±50 ppm
(≈180 ms/hour).

This engine aligns by chunk counting: loopback is the clock, mic is pulled from
a ring to match each loopback chunk, with stale-backlog drops and zero-fill
underruns. Replacing that with a PLL resampler is the highest-risk change in the
blueprint — it touches every sample of the mix.

## Decision

Split L2 into measurement (now) and control (later):

- **Now:** stamp `Stopwatch.GetTimestamp()` on every `AudioWriteJob`; anchor a
  session QPC clock plus a delivered-frame counter; expose wall-minus-stream
  drift in `HealthReport` via the tested `EngineHealth.ComputeDriftMs`.
- **Later (migration Phase 2):** close the loop — timestamp-ordered mixer,
  fractional resample cursor driven by the filtered skew, then retire
  chunk-counting and the stale-backlog guillotine.

## Consequences

- Good: we start *observing* drift in real sessions before writing the controller
  that compensates it — the PLL's ratio servo can be tuned against real data.
- Good: zero sample-path changes, so no listening-test risk in this pass.
- Bad: chunk-count creep and linear-resample aliasing remain until Phase 2.
