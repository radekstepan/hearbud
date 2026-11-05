# ADR-0002: Keep BlockingCollection with TryAdd (drop + count), don't adopt Channels yet

> **Superseded by [ADR-0012](0012-phase4-naudio-rewrite.md).** Phase 4 deleted
> `BlockingCollection`/`AudioWriteJob` with the write queue and adopted
> `Channel` + `DropOldest` (`CaptureHandoff.cs`) as this ADR anticipated.

Status: superseded by ADR-0012

## Context

The blueprint's L0 rule: audio threads must never block — copy, stamp, hand off,
and if the queue is full, drop and count. It prescribes `System.Threading.Channels`
with `DropOldest`.

This engine already honors the *rule* if not the *type*: `EnqueueWrite` uses
`BlockingCollection.TryAdd` (never the blocking `Add`), returns the rented
buffer on failure, and counts drops in `_droppedBlocks` with throttled logging.

## Decision

Keep `BlockingCollection<AudioWriteJob>` + `TryAdd`, and strengthen the surrounding
contract instead of swapping the queue type:

- Every job now carries a QPC timestamp stamped at handoff (`AudioWriteJob.QpcTick`)
  as the foundation for timestamp-based sync (ADR-0005).
- Drop accounting was fixed: `_droppedBlocks` (and `_writerException`) reset per
  session in `StartAsync`; previously they accumulated across sessions on a reused
  engine.
- Drops are now visible in the ~1 Hz `HealthReport` (ADR-0004), not just the stop log.

## Consequences

- Good: no threading-model churn; the existing drain-on-stop logic
  (`CompleteAdding` + 30 s timeout) is untouched.
- Bad: `BlockingCollection` is heavier than `Channel` and `DropOldest` semantics
  (drop the *oldest* vs refuse the *newest*) would preserve fresher audio under
  sustained stalls. Revisit with the mixer-thread refactor (migration Phase 2).
