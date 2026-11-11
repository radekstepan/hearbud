# Edge-Case Matrix

Deterministic edge cases for the Hearbud recording pipeline: what the engine
does, which test pins it, and how to probe it by hand. All automated tests run
with `dotnet test` on Windows — no audio hardware needed (temp dirs, bare
`RecorderEngine` instances, and reflection over private members only).

Conventions per case:

- **Rule** — what Hearbud does, and where (file + member).
- **Test** — the xunit test that pins it.
- **Try** — a manual probe on a real machine.

Related: `docs/WORKING_WITH_AUDIO.md` (DSP/clock background),
`docs/CONTRIBUTING.md` (Testing Guidelines), `docs/ARCHITECTURE.md`.

## 1. Different sample rates (e.g. 44.1 kHz mic vs 48 kHz loopback)

- **Rule:** Loopback is the master clock and sets the output rate; mic audio
  is resampled to match in `RecorderEngine.ConvertToTargetChunked`, with phase
  carried across callbacks in the private `ResamplerState` so chunk boundaries
  never click.
- **Test:** `RecorderEngineDspTests.ChunkedVsSingle_NoClick_AtSeams`,
  `Sinc_ChunkedVsSingle_BitIdentical`, `CumulativeLength_WithinOneFramePerSecond`,
  `ConvertToTarget_Resampling_OutputFrameCountIsCorrect`.
- **Try:** Set Windows mic to 44.1 kHz and speakers to 48 kHz, record 30 s of
  speech over music, and confirm the mix stays in sync end to end.

## 2. Clock drift 0.1–0.5% (mic faster or slower than loopback)

- **Rule:** sync pairs by count and absorbs steady offsets as latency —
  `TryPopFixedFrame` consumes take-oldest, nothing drops or pads. Only
  sustained backlog ≥ 80 ms over 3 ticks falls through to `CheckLagResync`,
  which drops the mic backlog to one fixed frame and counts a resync.
- **Test:** `RecorderEngineDspTests.CheckLagResync_SustainedBacklog_DropsToOneFrame`,
  `CheckLagResync_BelowThreshold_NoResync`,
  `RecorderEngineMixerTests.PairByCount_SyntheticTake_NoSplices_NoDrops`
  (synthetic 13 s take at 003933 gains: silent→system→silent, asserts the
  mix equals the gained sum with zero jumps, zero shortfalls, zero resyncs),
  `RecorderEnginePairByCountTests` (aligned / fully-ahead / far-ahead all
  pair by count; resync never triggers on stamp lead with healthy depth),
  `TakeVerifierTests` (clean sine passes; embedded pad and frame-edge steps fail).
- **Try:** Record 30+ minutes, then check the session `*.txt` log: backlog
  hovers near the natural device offset and no `resync #` warnings appear.
  For a byte-level gate run `hearbud verify-take <recordings-dir>` — it
  scans the take mix for pads and frame-edge jumps (exit 0 clean, 1 splice).

## 3. Jitter / bursts (variable-size WASAPI packets)

- **Rule:** Both sources land in dual FIFOs and are consumed strictly in
  480-frame quanta (`FixedFrameFloats`); `TryPopFixedFrame` emits one fixed
  frame per master tick regardless of arrival sizes, and `ReadExactSamples`
  loops short reads until the request is filled.
- **Test:** `RecorderEngineChaosTests.JitterBurst_VariablePackets_FixedFrameEmitWithSilencePad`,
  `RecorderEngineDspTests.ReadExactSamples_ShortRead_LoopsUntilAllRead`,
  `DualFifo_FixedFrame_RoundTrips`.
- **Try:** Record with a bursty source (Bluetooth mic, loaded CPU) and confirm
  the mix has no pitch wobble or frame tears — only brief padded silence
  under extreme starvation.

## 4. Overrun (mic faster than drain): drop-oldest + count

- **Rule:** `RecorderEngine.EnsureRingCapacity_NoLog_NeedsLock` caps the mic
  ring at `max(length, rate × channels × 2)`; demand beyond the cap drops the
  oldest samples and increments `_micOverrunBlocks`. Newest audio is always
  kept, so recovery is immediate once the source slows down.
- **Test:** `RecorderEngineChaosTests.OverrunDropOldest_DropsOldestCountsAndKeepsNewest`,
  `RecorderEngineDspTests.EnsureRingCapacity_Bounded_DropsOldestAndCounts`.
- **Try:** Saturate the machine during a recording, then check the stop
  summary line (`Overruns:`) in the session log — it must be nonzero rather
  than silent, and post-spike audio must be current, not stale.

## 5. Underrun (mic slower than drain): silence-pad + count

- **Rule:** `RecorderEngine.TryPopFixedFrame` zero-fills any mic shortfall so
  the fixed frame stays on time, incrementing `_micUnderrunBlocks` and
  `_silencedSamples`. If loopback itself goes silent for > 200 ms
  (`LoopSilentMsThreshold`), the mic drives output alone and the ring is
  cleared on loopback resume so old mic audio never plays over new system
  audio.
- **Test:** `TryPopFixedFrame_MicShortfall_ZeroFillsAndCountsUnderrun`
  (`RecorderEngineDspTests`); the pad half of
  `RecorderEngineChaosTests.JitterBurst_VariablePackets_FixedFrameEmitWithSilencePad`.
- **Try:** Record 10 s of mic-only speech with no system audio — `mic.wav`
  must contain speech, `system.wav` silence — then start playback and confirm
  sync restarts cleanly.

## 6. Clipping (hot gains): headroom + memoryless cap + clamp + mix CLIP light

- **Rule:** `RecorderEngine.EmitFixedMix` sums with fixed -6 dB headroom
  (`* 0.5`: two hot sources routinely sum past 0 dBFS), `ApplyMixLimit`
  caps memorylessly on overs (1/peak per frame, no release envelope so
  overs never duck following audio), `SoftClipIfNeeded`
  applies tanh above ±1, and `FloatToPcm16/32` clamp to the integer range
  (16-bit adds ±1 LSB TPDF dither). The chain cannot wrap around no
  matter the gain. A mix-bus meter (`LevelSource.Mix`,
  post-gain/post-headroom) drives a third CLIP light so persistent overs
  tell the user to turn the gains down instead of being ducked away.
- **Test:** `RecorderEngineChaosTests.GainSweep_MixScaleAndSoftClip_NoWrapAround`
  (gains 0–3 × probes to ±100),
  `RecorderEngineDspTests.ComputeMixScale_NormalizesOnlyOnOvers`,
  `MixCap_HotFrame_CapsAtCeiling_NoStickyDuck`,
  `MixCap_SustainedOvers_StayAtCeiling_QuietRecoversInstantly`,
  `MixMeter_ReportsPostGainPeak_AndClipsOnOvers`,
  `SoftClip_MatchesTanh_OnlyAboveOne`, `FloatToPcm32_Clipping_ClampsToIntMax`,
  `FloatToPcm16_Clipping_ClampsToShortRange`.
- **Try:** Set mic and loop gain to max (3.0x / 3.0x), record loud
  audio, and confirm the mix never exceeds 0 dBFS — no ducking of following
  audio — with the Mix clip indicator as the cue to back the gains off.

## 7. Mute / volume changes mid-record

- **Rule:** `MicGain`/`LoopGain` and volatile `MicMuted`/`SystemMuted` flags
  are snapshotted per fixed frame in `EmitFixedMix`/`EmitMicOnlyFixed`, so
  changes take effect on the next frame with no teardown, no state reset, and
  no click.
- **Test:** `RecorderEngineMuteTests.EmitFixedMix_MicMuted_EqualsLoopOnly`,
  `EmitFixedMix_SystemMuted_EqualsMicOnly`, `EmitFixedMix_BothMuted_IsSilence`,
  `EmitMicOnlyFixed_MicMuted_IsSilence`.
- **Try:** While recording, drag the gains; the meters and the resulting
  WAVs must follow within a frame. (The include checkboxes only mute while
  idle — toggles are skipped once recording starts, since sources are fixed
  for the running file.) Unmuting while idle must not burst or gap.

## 8. Acoustic echo (speakers feeding the mic)

- **Rule:** Out of scope — there is no AEC in `RecorderEngine.cs`. Record
  with headphones. (The ring-clear on loopback resume in case 5 prevents a
  *recorded-timing* echo artifact, which is a different, handled issue.)
- **Test:** None — acoustic coupling needs a room, not a unit test.
- **Try:** Monitor through speakers with a live mic and confirm you *can*
  produce feedback; then switch to headphones and confirm the recording is
  clean. If Hearbud ever gains AEC, it gets a case here.

## 9. Device unplugged mid-record: silence-bridge + reopen

- **Rule:** A lost source is detached while the surviving source keeps the
  session alive on silence (`HandleDeviceDisconnectedAsync` / `DetachSource`
  in `RecorderEngine.cs`; the loopback-silent bridge in case 5 covers the
  timing side). `FinalizeWavFiles` keeps headers valid throughout, and
  `TryResumeLostSourcesAsync` / `ReopenLostSourceLoopAsync` reattach the
  device into the same files when it comes back.
- **Test:** None automated — device loss is hardware-gated.
- **Try:** Unplug the mic 10 s into a recording, speak, plug it back in, then
  stop: all three WAVs must be intact and playable, with silence (not garbage
  or a truncated file) bridging the gap. See also `docs/CONTRIBUTING.md`
  device-change checklist.

## 10. MMCSS / scheduling (capture thread starved by CPU load)

- **Rule:** `RecorderEngine.EnsureCaptureBoost` / `ReleaseCaptureMmcss`
  (no-op off Windows) prioritize capture threads; `RecordCallbackTiming`
  counts a glitch only when a source gap exceeds 50 ms, surfaced via
  `CaptureGlitches`.
- **Test:** `RecorderEngineDspTests.Capture_RecordTiming_FirstCall_NoGlitch_ThenGap_Counts`,
  `Capture_BoostAndRelease_AreSafe_Idempotent`,
  `NativeTiming_BeginEnd_RefCounted_Idempotent`.
- **Try:** Record while running a heavy build; the session log may show
  occasional glitch counts, but audio must stay continuous (see case 3/5
  padding) rather than tearing.

## 11. Mux / trailer validity (crash before headers finalize)

- **Rule:** WAV sizes are placeholder until `FinalizeWavFiles` rewrites them
  on stop/dispose. A crash leaves them invalid; `WavRepair.TryRepair` fixes
  RIFF/data sizes in place from actual file length (`WavRepair.cs`), and
  `SessionManifest` tracks `closedCleanly` per session with `ScanUnclosed` /
  `RepairUnclosed` auditing the outcome.
- **Test:** `RecorderEngineChaosTests.MuxTrailer_WavRepair_FlagsTruncatedHeaders`,
  `WriterFailureTests.Manifest_Begin_Unclosed_ScanFinds_MarkClosed_ScanClears`,
  `Manifest_CorruptJson_Ignored_BestEffort`, plus the
  `WriterHealth.IsDiskFull` / high-water suite for the disk-full stop path.
- **Try:** Kill the app mid-record, then confirm `WavRepair.TryRepair`
  restores playability and `SessionManifest.ScanUnclosed` still flags the
  session until `MarkClosed`. (There is currently no automatic startup
  scan in the UI — recovery is covered by the tests above, not by a
  restart flow.)
