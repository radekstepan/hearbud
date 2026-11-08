---
"hearbud": patch
---

Capture-starvation watchdog: auto-reopen wedged devices in the same file

RecExperts `LocalRecordGraph::RestartGraph` restarts the graph instead of
letting a dead capture silently record hours of silence. Our 50ms
`CaptureGlitches` counter only observed, and the 1Hz lag log lives inside
`OnLoopbackData` so it dies with the callbacks.

- `RecorderEngine`: `IsCaptureStale` / `CheckCaptureStarvation` /
  `PollCaptureWatchdog` — fires when EVERY active source is callback-stale
  for >2s. Pause-aware, disposal-safe, skips while a reopen is in flight,
  10s cooldown. Detaches zombies + single `TryResumeLostSourcesAsync`;
  WAV writers/headers never touched.
- `Resume()` refreshes `_lastMicTick` too (was loop-only, false positive).
- `MainWindow.OnLimitTimerTick` polls the watchdog before its early return.
- Tests: `RecorderEngineWatchdogTests` (reflection-driven, no hardware).
