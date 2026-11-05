# ADR-0012: Phase 4 — net9 + NAudio v2 engine (rewrite, per-leg rebuild, RF64)

Status: accepted

## Context

Phases 0–3 hardened v1 in place; Phase 4 finishes the blueprint: CSCore is
gone, capture is NAudio 3.3 (`WasapiRecorder`), and the engine is the
single-mixer design. This was written and verified on macOS without audio
hardware or MSBuild, so every NAudio signature was proven from package metadata
(IL-level dump) before use, and all hardware-free paths execute green.

## Decision

1. **Retarget net9.0-windows; NAudio.Wasapi/Core 3.3.0 replace CSCore.**
   Verified real and stable before committing (split packages, signed, MIT).
   Two blueprint details were wrong and got fixed: `WithLowLatency` takes a
   bool, and NAudio QPC stamps are 100-ns units (not Stopwatch ticks) —
   `TimestampedMicBuffer` takes the clock frequency instead of converting.
2. **Capture AT 48 kHz float32 stereo on both legs** (loopback + raw-mode mic
   with cooked fallback, IAudioClient3, MMCSS "Pro Audio"). Wholesale
   resampling and int/PCM conversion are deleted, not ported
   (`ConvertToTarget`, `FloatToPcm*`, dither, `ReadExactSamples` + their tests).
3. **One mixer thread, loopback-paced.** 10 ms tick drains both channels; pulls
   all available loopback (mic via the timestamp buffer); loopback-idle + mic
   gives steady mic-only pulls; both idle pauses the session (v1 file
   semantics preserved). Meters, hum tap, and RF64 writes all happen here —
   the write queue, `BlockingCollection`, and `AudioWriteJob` are deleted.
4. **Callbacks do nothing** (copy → stamp → `TryWrite`, never throw).
   Per-block `float[]` allocation is deliberate: ~200 small Gen0 arrays/sec is
   negligible, and pooling would leak on every DropOldest eviction.
5. **Per-leg auto-rebuild replaces safe-stop.** `RecordingStopped` (error path;
   clean stops are flagged) triggers backoff rebuild of that leg only while
   the mixer coasts; `HandleDeviceDisconnectedAsync` is deleted.
   `DevicesChanged` + `AudioDeviceInfo` DTOs keep ALL WASAPI types inside the
   engine — the UI holds name→id maps and marshals one event.
6. **RF64 float32 everywhere + NAudio MP3 worker.** `StreamingWavWriter` gains
   an 80-byte RF64 mode (ds64 round-tripped through NAudio's own reader in
   tests); MP3 is `WaveFileReader → WaveToSampleProvider →
   SampleToWaveProvider16 → MediaFoundationEncoder.EncodeToMp3`
   (current non-obsolete chain; coarse 0/100 % progress, no mid-encode cancel).
7. **Linear drift correction stays.** With same-rate capture the PLL bends
   ±0.1 %, where linear interpolation is inaudible and provably stable; the
   Wdl swap's benefit is below measurement and can't be listening-tested here.
   The ratio servo and cursor are unchanged, so Wdl stays a pure inner-loop
   swap if anyone wants it.

## Consequences

- Good: 193/193 green on net9 (incl. RF64→NAudio round-trip, channel drops,
  mixer DSP); every NAudio call shape is compile-proven against the real DLLs.
- Good: MainWindow no longer references any audio API — the engine is headless.
- Bad: MainWindow's device-layer rewrite (~80 lines) and the XAML build could
  NOT be compile-checked here (WPF needs MSBuild on Windows) — first Windows
  build is the mandatory gate, before any listening test.
- Bad: no mid-MP3 progress/cancel (NAudio helper is synchronous).
- Bad: Windows gates still open: 1-hour dual capture, yank/replug/switch
  mid-take, 8-hour soak (drift < ±2 ms), blind A/B vs Phase 3.
