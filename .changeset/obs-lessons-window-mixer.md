---
"hearbud": patch
---

OBS lessons windowed pull and mixer thread

- What: `RecorderEngine` now pulls mic by timestamp window instead of take-oldest (stale prefix dropped capped, overlap consumed, tail zero-filled; resync triggers on a fully-ahead mic window), and all emits moved from the WASAPI callbacks to a dedicated 10 ms mixer thread with one emit path plus a private silence-stem buffer. Tests updated for the 3-arg `PushLoopRing`; new `RecorderEngineWindowedPullTests`/`RecorderEngineMixerTests`; `ARCHITECTURE.md` §5 + header comment updated.
- Why: finish `docs/OBS_LESSONS.md` items 3.4 and 3.3 — underruns become a real overlap metric, a stalled device can no longer block the mix, and the shared `_frameMic` race between callbacks is gone. Trim/resync/depth logic and file formats unchanged, with legacy fallback when tick cover is absent.
- Verification: test-project build 0 warnings; `dotnet test` 252 passed, 0 failed. Full-solution publish not runnable locally (no network for runtime packs, .NET 8 SDK absent); CI is the gate.
