---
"hearbud": patch
---

OBS lessons clock and silence guards

- What: RecorderEngine stamps loopback/mic packet starts on the shared Stopwatch/QPC clock and logs loop-minus-mic offset (`clkErr`/`clkMax`) on the 1 Hz line (diagnostics only, no trim behavior change); adds an explicit silent-loopback detector that skips drift trim while the desktop is quiet; default speaker pick now uses `Role.Console` to match OBS. New `RecorderEngineObsLessonsTests` covers the clock helper, the silence detector, and the error bookkeeping.
- Why: follow `docs/OBS_LESSONS.md` sequencing steps 1-2 (3.1 timestamps + log, 3.2 silence detect) plus small item 3.5; makes drift measurable and stops quiet-desktop false trims. Mixer-thread (3.3) and windowed pull (3.4) deferred per the doc's own sequencing.
- Verification: `dotnet build` + `dotnet test` on the test project green (243 passed, 0 failed); full-solution publish not runnable locally (no network for runtime packs, .NET 8 SDK absent) so CI is the gate for that.
