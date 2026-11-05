# ADR-0003: Crash-safe persistence via tmp + atomic rename (defer RF64 / header re-patch)

> **Partially superseded.** The RF64 / header re-patch deferral was lifted by
> [ADR-0011](0011-phase3-writer.md) (owned writer) and
> [ADR-0012](0012-phase4-naudio-rewrite.md) (RF64 mode); the orphan sweep
> landed per [ADR-0009](0009-phase1-wiring.md). Tmp + atomic rename and the
> disk preflight remain in force. Links to `docs/v2/` are historical — that
> folder was removed after the migration landed.

Status: partially superseded (see ADR-0009, ADR-0011, ADR-0012)

## Context

The blueprint's L3 principle: a crash mid-take must leave playable files —
RF64 containers, headers re-patched every ~2 s, record-to-tmp then atomic rename.

This engine uses CSCore's `WaveWriter`, which owns its stream and only finalizes
RIFF headers on `Dispose`. We cannot re-patch headers periodically without
replacing the writer, and sessions are still capped by 32-bit RIFF sizes (~4 GB).

## Decision

Implement the two L3 steps that don't require owning the writer:

1. **Tmp + atomic rename.** Writers target `<final>.tmp` during capture;
   `StopAsync` disposes (finalizing headers) then publishes via
   `CrashSafeFiles.AtomicPublish`, which re-checks uniqueness at move time and
   uses `File.Move` (atomic on NTFS). Users never see half-named sessions, and a
   `Dispose`-without-`Stop` path leaves valid, recoverable `.tmp` files
   (rename to `.wav` to recover).
2. **Disk-space preflight.** `StartAsync` refuses to start below 20 MB free and
   warns below 100 MB (or ~5 min of estimated bitrate). The pure rule
   (`EvaluateSpace`) is unit-tested; the `DriveInfo` wrapper fails open.

Deferred: RF64, periodic header re-flush, and kill -9 survivability. A hard kill
can still strand a `.tmp` with unpatched headers — documented in code and in
[docs/v2/MIGRATION_PLAN.md](../v2/MIGRATION_PLAN.md) Phase 3.

## Consequences

- Good: no more zero-byte *final* files from mid-session failures; full disks
  fail fast with a clear message instead of dying at minute 30.
- Good: all new logic lives in the hardware-free, tested `CrashSafeFiles` class.
- Bad: `.tmp` leftovers after a crash need manual renaming; no automatic recovery
  sweep yet (cheap future addition: on startup, offer to publish orphaned `.tmp`).
