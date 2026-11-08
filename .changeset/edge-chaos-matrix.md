---
"hearbud": patch
---

Edge-case matrix + chaos tests (ere-core EDGE_CASES.md parity)

Hearbud had no single place stating what happens under clock drift, jitter,
overruns, hot gains, device loss, or a crash before headers finalize.

- `docs/EDGE_CASES.md` (new): 11-case matrix adapted from ere-core. Each case
  has a Rule (what Hearbud does, citing `RecorderEngine.cs` /
  `WriterHealth.cs` / `SessionManifest.cs` / `WavRepair.cs`), a Test (which
  xunit test pins it), and a Try (manual probe). Acoustic echo is explicitly
  marked out-of-scope (no AEC); device-loss reopen and crash recovery are
  manual-probe only (hardware-gated).
- `Hearbud.Tests/RecorderEngineChaosTests` (new): 5 deterministic tests, no
  hardware, temp dirs only — reflection over verified private members only
  (`TryGentleTrim`, `TryPopFixedFrame`, `PushLoopRing`,
  `FixedFrameFloats`, `EnsureRingCapacity`, `ComputeMixScale`,
  `SoftClipIfNeeded`) plus public `WavRepair` APIs:
  drift absorption (0.5%-fast mic held by trim under the 80 ms resync
  threshold), jitter bursts (variable packets, fixed-frame emit with
  silence-pad + underrun count), overrun drop-oldest (bounded, counted,
  newest kept), gain sweep 0–3x (no wrap-around, output in [-1, 1]),
  truncated-WAV detection/refusal.
- What: docs + tests only, no production code changes.
- Why: parity with ere-core edge-case coverage; locks in drift/jitter/
  overrun/limiter/trailer behavior before further pipeline work.
- Verification: `dotnet test` needs Windows CI — dotnet is not available on
  this Linux box, so tests were NOT executed locally. New tests mirror
  assertion patterns already proven in `RecorderEngineDspTests` /
  `WriterFailureTests`, but treat CI as the gate.
