---
"hearbud": patch
---

Writer-failure hardening (RecExperts FileMuxer/FixupFile parity)

`DiskWriteLoop` swallowed per-file errors and only surfaced queue-level
fatals at stop; ENOSPC mid-session was indistinguishable; no warning before
the queue started dropping; a crash without Dispose left no session record.

- `WriterHealth` / `WriterFaultTracker` (new): sticky per-target counters +
  first-error capture; ENOSPC detection (HResult 0x70/0x27 + message
  fallback, inner-chain walk); 75% high-water rule.
- `RecorderEngine`: per-job errors recorded; disk-full latches an immediate
  graceful stop (intake halts, queued audio drains so headers finalize);
  fault summary in log + final status; high-water Warn once per session;
  `CompleteAdding` tolerant of writer-thread completion.
- `SessionManifest` (new): `<base>.hearbud-session.json` sidecar,
  `closedCleanly` flag, paths/bytes/error summary; `RepairUnclosed` fixes
  listed WAVs via `WavRepair` and audits the outcome in-manifest.
- Tests: `WriterFailureTests` — HResult/message/inner-chain, sticky
  counters, latch-once, high-water boundary, manifest round-trip + corrupt
  JSON tolerance (temp dirs only, no hardware).
