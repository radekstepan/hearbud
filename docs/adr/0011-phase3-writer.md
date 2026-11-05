# ADR-0011: Phase 3 — owned RIFF writer with 2 s header re-patch

Status: accepted

## Context

CSCore's `WaveWriter` only finalizes headers on `Dispose`, so any mid-take
crash/kill/power cut strands unplayable files. The blueprint wants RF64 +
re-patch + tmp/rename; tmp/rename already landed (ADR-0003).

## Decision

1. **`StreamingWavWriter` replaces `WaveWriter`** (same `Write`/`Dispose`
   shape, so the engine swap is mechanical). It re-patches the 44-byte RIFF
   header every 2 s over a `WriteThrough` stream: the file on disk is a valid,
   playable WAV at any instant — a kill loses ≤2 s of tail, never the take.
   Mid-stream validity is proven by a "crash simulation" test that parses the
   file while the writer is still open.
2. **Stay RIFF (32-bit sizes) for now.** Past ~4 GB sizes saturate (truncated
   but playable prefix) and the engine warns loudly with a split-the-session
   message. RF64 waits for Phase 4 because the MP3 step still reads through
   CSCore's `WaveFileReader`, which cannot parse RF64.
3. **Format-explicit PCM.** Both 16- and 32-bit paths write integer PCM
   (format tag 1, never float) — matching what `FloatToPcm16/32` produce.
4. **Compat-guarded handoff.** A test round-trips our bytes through the real
   CSCore `WaveFileReader` (format fields + byte-exact payload), proving the
   MP3 step sees exactly what it saw before.

## Consequences

- Good: kill -9 mid-take now leaves three playable WAVs (minus ≤2 s tail).
- Good: 207/207 green, including 9 new writer tests.
- Bad: the 4 GB wall stands until Phase 4 (mitigated: loud warn at 3.8 GB).
- Bad: `WriteThrough` costs some I/O throughput on slow media — absorbed by
  the drop-counting queue, and correctness beats speed for masters.
