---
"hearbud": patch
---

Static invariants on the audio engine (ere-core static_assert parity).

## What
- `Hearbud/RecorderEngine.cs` (Debug.Assert additions only, no behavior change):
  - `FixedFrameFloats()`: asserts result equals `FixedFramesPerChannel * max(1,_outChannels)` and is channel-aligned.
  - `EnsureRingCapacity` / `EnsureRingCapacity_NoLog_NeedsLock`: entry asserts `roomNeeded >= 0`; exit asserts `_micCount <= _micRing.Length` on every return path.
  - `TryPopFixedFrame`: post-guard assert that `loopDst`/`micDst` hold `>= n` floats.
  - `CheckLagResync`: asserts `ResyncThresholdSec == 0.08`, documenting the 80 ms contract with the watchdog tests.
- `using System.Diagnostics;` was already present (line 43); no using added.

## Why
Compile-time-free documentation of ring/frame invariants that ere-core enforces with `static_assert`, so Debug builds fail fast on accounting regressions instead of silently corrupting the mix.

## Verification
Not compiled locally: `dotnet` is absent on this Linux box, so CI `dotnet test` on the Windows runner is the gate. Note for the reviewer: `Debug.Assert` is a no-op in Release unless DEBUG is defined — these asserts are dev-only documentation as committed; enable DEBUG asserts explicitly if CI should enforce them.
