---
"hearbud": patch
---

5 ms resume fade-in on loop-driven mix joins

- What: `DoMixerTick` arms a 5 ms linear fade when silence flips to
  signal (and at record start via `_prevLoopSilent=true`); `EmitFixedMix`
  applies it per audio frame before the peak/cap pass so cap, mix meter,
  and file stay consistent. Mixer-thread state only, no devices touched.
  New `ResumeFade_FirstFramesRamp_NoTick` test locks the ramp (starts at
  ~0, half at mid-fade, transparent after 240 frames at 48 kHz).
- Why: the unpause join tick is a real device gap plus stale buffer plus
  limiter attack landing in ~20 ms; the fade makes it inaudible instead
  of a short buzz.
- Verification: `dotnet test Hearbud.Tests -c Release` green (276/276,
  incl. 1 new test).
