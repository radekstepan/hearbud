---
"hearbud": patch
---

Resampler validation hardening (tests-only, no prod change)

Pins the custom 12-tap Blackman-Harris polyphase resampler
(`SincPolyphase` + `ConvertToTargetChunked`) to libswresample-class quality,
mirroring the RecExperts reference (`swr_convert` + `swr_get_delay`).

New `Hearbud.Tests/RecorderEngineResamplerQualityTests.cs`
(reflection-only, no hardware): random-split bit-identity (mono/stereo,
1-sample torture), sine flatness + above-Nyquist boundedness, DC step /
impulse / measured group-delay, Totals ratio convergence, ITU fold
spot-checks (3/4/5/8ch, mono→quad). Zero production-code changes.
