# ADR-0008: Exponential backoff for device-open; safe-stop on disconnect stays (defer per-leg rebuild)

> **Partially superseded by [ADR-0012](0012-phase4-naudio-rewrite.md).**
> Mid-session safe-stop was replaced by per-leg auto-rebuild (the mixer
> coasts while the lost leg is rebuilt); exponential backoff survives in the
> rebuild path. `HandleDeviceDisconnectedAsync` is deleted.

Status: partially superseded by ADR-0012

## Context

The blueprint's supervisor rebuilds a lost leg with backoff while the mixer
coasts on zeros — yanking a USB mic must not kill the session. Today this engine
retries invalidated-device opens 3× at a fixed 250 ms, and `HandleDeviceDisconnectedAsync`
(wired from the UI's `IMMNotificationClient`) *stops the session safely* (drains
the queue, finalizes headers) rather than recovering it.

True per-leg rebuild needs the mixer to coast without one `SoundInSource`, which
the current callback-coupled design can't do: tearing down one leg tears down
the sync.

## Decision

- **Now:** exponential backoff on device-open (250 ms → 2 s cap, 5 attempts,
  per-attempt logging) for the common transient case (USB re-enumeration,
  Bluetooth reconnect at startup). Keep safe-stop-on-disconnect as the
  mid-session behavior — data safety first.
- **Later (migration Phase 4):** per-leg rebuild with backoff on top of the
  timestamp-ordered mixer, which can coast on zeros by construction.

## Consequences

- Good: cheap reliability win for the flakiest moment (startup races); no sync risk.
- Bad: mid-session device loss still ends the take (safely, with files intact).
