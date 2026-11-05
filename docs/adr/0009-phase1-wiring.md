# ADR-0009: Phase 1 — hum watchdog live, health in UI, orphan sweep

Status: accepted (supersedes the "hum staged" half of ADR-0007)

## Context

Phase 0 staged `Goertzel`/`HumWatch` without wiring them, and the `Health`
event had no consumer. Crash leftovers (`*.tmp`) also had no recovery story.

## Decision

1. **Hum watchdog wired, lock-free.** One `HumWatch` per audio thread
   (Goertzel state is not thread-safe): the loop thread pushes the recorded
   mix, the mic thread pushes converted mic; each polls its own on a 1 s
   rhythm. The mic result crosses to the loop thread via two plain fields read
   into the combined `HealthReport` (new `HumDetected`/`HumFreqHz` fields).
   A rising-edge `Warn` points at the hum triage in
   `../TROUBLESHOOTING.md#steady-hum-or-buzz-in-the-mic-take`. Per-sample
   cost is a few flops piggybacked on buffers already in cache.
2. **Health surfaced in the UI (code-behind only, no XAML changes).**
   `MainWindow` stashes the 1 Hz report, appends hum/drop suffixes to the tray
   tooltip, and shows mains hum in the status line while it persists
   (restoring Recording…/Monitoring… when it clears).
3. **Orphan sweep on startup.** `CrashSafeFiles.FindOrphanedTmp` /
   `RecoverOrphanedTmp` auto-publish crash leftovers with unique names; the UI
   shows a rare info dialog so recovery is never silent. The sweep can never
   break startup (fail-open, wrapped in try/catch).

## Consequences

- Good: the buzz triage is now instrumented end to end — detector, report,
  log, and UI — and crash leftovers self-heal.
- Good: all new logic is hardware-free and tested, including the
  `MaybeEmitHealth` merge via reflection.
- Bad: warn thresholds (0.25 s backlog, 50 ms drift, −50 dB hum) are still
  judgment calls — tuning needs real-session data (Windows gate).
- Bad: `SoftLimiter` stays staged; its swap is Phase 2 (sample-path change).
