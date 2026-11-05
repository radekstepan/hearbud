# ADR-0004: Supervise via a ~1 Hz HealthReport event (log only when degraded)

Status: accepted

## Context

The blueprint's L4 principle: nothing fails silently — dropped blocks, drift,
backlog, and hum get a 1 Hz health report. Previously this engine logged mic
backlog every 50 loopback blocks (~2 Hz, always) and reported drops/underruns
only in the stop summary. There was no live signal a UI (or watchdog) could bind to.

## Decision

Add `RecorderEngine.Health` (`EventHandler<HealthReport>`, ~1 Hz, emitted from
the loopback callback via a cheap tick check):

- `HealthReport`: dropped blocks, mic underruns, current/peak backlog seconds,
  loopback block count, QPC-vs-stream drift in ms, UTC timestamp.
- Emission cost on the audio thread is a few integer ops when idle; subscriber
  exceptions are swallowed so UI bugs can't kill audio.
- Log discipline: steady-state health stays *out* of the session log; degraded
  states (any drops, backlog > 0.25 s, |drift| > 50 ms) emit one `Warn` per second.
- The drift math (`EngineHealth.ComputeDriftMs`) is pure and unit-tested,
  including a ±50 ppm crystal-drift case.

## Consequences

- Good: first live supervision signal; the WPF UI can later bind meters/health
  without touching the audio path (no UI changes in this pass by design).
- Good: quieter logs in the healthy case, louder exactly when it matters.
- Bad: thresholds (0.25 s, 50 ms) are judgment calls, not measured — tune after
  real-session data.
