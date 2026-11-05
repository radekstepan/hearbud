# ADR-0006: MP3 stays in-process, post-finalize (defer the ffmpeg sidecar)

Status: accepted

## Context

The blueprint moves MP3 encoding out-of-process (NAudio.MF or a bundled ffmpeg
sidecar) so an encoder hang/failure can never endanger the masters, and calls
v1's in-process MediaFoundation encode through CSCore "the most brittle post-step".

In this engine, MP3 encoding already runs strictly *after* all three WAVs are
disposed (headers finalized) and published, on a worker task, with cancellation
support and progress events. An encoder failure today loses only the `.mp3`.

## Decision

Keep in-process MediaFoundation encoding; the tmp→publish ordering (ADR-0003)
holds the property the blueprint cares about (masters safe before encode).
Defer the sidecar to migration Phase 4, alongside the device-rebuild work that
shares the "supervised worker" machinery.

## Consequences

- Good: no new deployment artifact (no ffmpeg binary to ship/sign) and no
  behavior change.
- Bad: a MediaFoundation hang still hangs `StopAsync` (mitigated: cancellation
  token) and CSCore-MF stays the least-maintained link.
