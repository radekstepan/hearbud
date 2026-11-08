---
"hearbud": patch
---

Per-frame mute: MicMuted/SystemMuted (RecExperts SetFile*Mute parity)

Hearbud had no mid-record mute — IncludeMic is start-time device selection.
RecExperts applies mute as a per-frame gain with zero-fill of that channel.

- `RecorderEngine`: `MicMuted`/`SystemMuted` (volatile int, Volatile R/W
  like gains) + `SetChannelMute(bool, bool)`. One Volatile read per fixed
  frame; no lock, no alloc, pause-safe. Log line per toggle with masterFrames.
- `EmitFixedMix`/`EmitMicOnlyFixed`: muted channel contributes 0f; gains
  hoisted out of the per-sample loop. Meters tap post-mute samples (live 0).
- Stem WAVs deliberately stay raw (mute is mix-time, archival preserved).
- Tests: `RecorderEngineMuteTests` — defaults, round-trip, mic==loop-only,
  sys==mic-only, both==silence, mic-only-path, mid-stream toggle on/off.
