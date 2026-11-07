# RecExperts 5.1.9 vs Hearbud — Gap Analysis and Fix Plan

> Source truth: `/home/radek/dev/easeus recexperts/` (unpacked 5.1.9 installer + `RECEEXPERTS_ANALYSIS.md` + `ere-analysis/` + `ere-core/` + `ere-ui/`) vs `/home/radek/dev/hearbud/` (`Hearbud/RecorderEngine.cs`, `Hearbud/MainWindow.*`, `docs/ARCHITECTURE.md`).
> Date: 2026-11-07. No code changed — analysis only.

---

## 1. What was compared

### 1.1 Original: EaseUS RecExperts 5.1.9

Unpack chain (see `RECEEXPERTS_ANALYSIS.md:1-58`):

```text
ere_brand_ad_Installer_*.exe (NSIS 3.03 stub, 1.6 MB)
→ EDownLoader.exe (2 MB, DuiLib + libcurl, reads downloader_x86.ini)
→ screenrecorder5.1.9_brand_ad.exe (Inno Setup, 133 MiB, MD5 111FAA74D730C08538166D002CF67D6D)
→ innoextract → recexperts_unpacked_app/bin/
    RecExperts.exe (2.7 MB, Qt 5.15.2 QML UI)
    RecordExecutor.exe (1.6 MB, out-of-proc gRPC server ERE.Record/*)
    RecordScreen.dll (3.3 MB, capture engine)
    WinRtCapture.dll, AmdVideo/NvidiaVideo/IntelVideo.dll,
    EreConverter.dll, ErePlayerCore.dll, EreTaskScheduler.dll, ...
```

Language: C++ with Qt 5.15.2 QML, MSVC 14.16, x86 (`RECEEXPERTS_ANALYSIS.md:60-75`).

Audio path (see `RECEEXPERTS_ANALYSIS.md:76-98`):

```text
RecExperts.exe (microphoneEnable/microphoneId, speakerEnable/speakerId toggles)
→ RecordExecutor.exe (gRPC ERE.Record/CtrlRecordState, Get|SetFileMicrophoneMute, .../SpeakerMute, SetRecordVolume)
→ RecordScreen.dll: WASAudioCapture → AudioResampleConvert → PCMMixer → AudioAmix → FileMuxer
```

Key engine classes/strings (all verified in binary, see `RECEEXPERTS_ANALYSIS.md:90-97`):

* `WASAudioCapture::Initialize / InitializeRender / CaptureThread`
* `WASAudioCapture::CaptureThread`, `WASAPI can't get render|capture client service`
* `WinAudioAecDmo::Start`, `WebRTCWrapper::InitializeMicrophoneWaveFmt`
* `qAudioResampleConvert::SetWaveFmt`, `AudioResampleConvert::ProcessPcm`
* `PCMMixer::Initialize / OnGotPcmData / SetMicrophoneVolume / SetSpeakerVolume`
* `AudioAmix::Initialize / AddOuputFilter / ApplyVolumeLevel2Graph`
* Filters: `amix=inputs=%d`, `[in0]volume=%f[in0];[in1]volume=%f[in1];[in0][in1]amix=inputs=2[out]`
* Debug taps: `%s/microphoneIn.pcm`, `%s/speakerIn.pcm`, `%s/mixOut.pcm`, `%s/webrtc_out.pcm`

Clean-room recreations in this workspace:

* `ere-analysis/decompiled_audio.c:1-81` — pseudocode for `AudioAmix::Initialize`, `PCMMixer::OnGotPcmData`, `WASAudioCapture` init sequence.
* `ere-analysis/LEARN_AUDIO_SYNC.md:1-30` — 5-idea toy model.
* `ere-analysis/EDGE_CASES.md:1-92` — 11 edge cases with binary string refs.
* `ere-core/src/*.cpp` + `include/ere/*` — portable sync core (`graph.cpp` orchestrates).
* `ere-ui/orig/qml/**` + `ere-ui/runner/BACKEND_API.md:1-195` — full QML backend API surface.

### 1.2 Ours: Hearbud

* `Hearbud/RecorderEngine.cs` (~1100+ lines) — WASAPI loopback (`WasapiLoopbackCapture`) + mic (`WasapiCapture`) via CSCore, loopback-as-clock, mic ring buffer, async `BlockingCollection` disk writer, `ArrayPool` buffers.
* `Hearbud/MainWindow.xaml(.cs)` — WPF: device combos, gains, output folder, MP3 bitrate, meters, tray, Ctrl+R/S, `IMMNotificationClient` for plug/unplug.
* `docs/ARCHITECTURE.md` — threading, data flow, design decisions.
* Outputs: `*-system.wav` (16-bit), `*-mic.wav` (16-bit), `*-mix.wav` (32-bit), post `*.mp3`.

---

## 2. Architecture difference at a glance

| Aspect | RecExperts 5.1.9 | Hearbud | Verdict |
|---|---|---|---|
| Language/runtime | C++ Qt 5.15.2 QML, native FFmpeg/swresample, AVRT MMCSS | C# .NET 8 WPF, CSCore, MediaFoundation | Different stack; lessons port, code doesn't |
| Process model | UI (`RecExperts.exe`) + capture host (`RecordExecutor.exe`, gRPC) isolates crashes | Single process; capture dies with UI | Gap — see §6 |
| Capture threads | 2x event-WASAPI, `SetEventHandle`, MMCSS "Pro Audio", `timeBeginPeriod(1)` | CSCore defaults, no MMCSS/priority boost | Gap — see §3 |
| Sync model | Symmetric: resample-on-arrival → per-source `av_audio_fifo` → fixed-frame filter pull → master `av_gettime` clock | Asymmetric master/slave: loopback tick pulls mic ring; single FIFO; variable chunks to mix | Gap — see §3 |
| Resampler | `swr_convert` + `swr_get_delay` + `aresample,aformat` | Linear interpolation in `ConvertToTarget` | Gap — see §4 |
| Mix | `volume:eval=frame` per input + `amix=inputs=2` normalize + clamp | `(s+m)*0.5` + global `tanh` | Bug — see §5 |
| AEC/NS/AGC | `WinAudioAecDmo` + `WebRTCWrapper` | None | Gap — see §7 |
| Device loss | `OnLostDevice/RestartGraph/ChangeAudioDeviceInRecording`, continue same file, `FixupFile` | Stop recording on `AUDCLNT_ERR_DEVICE_INVALIDATED` | Gap — see §8 |
| Mux | AAC in MP4/MKV, `av_interleaved_write_frame`, always `av_write_trailer` | 3x WAV + post MP3 | Design choice — see §9 |
| Diagnostics | `.pcm` stage dumps, per-second `lagTime`, `EncodeSpan` | Session `.txt` log, `Mic ring backlog`, underrun counts | Partial — see §10 |
| Features | Pause, countdown, auto-stop/split, schedule, hotkeys, device test, tray states, converter/player | Start/stop, gains, bitrate, basic tray, Ctrl+R/S | Gap — see §11 |

---

## 3. GAP-01 — Capture timing and sync core (P0, correctness)

### 3.1 Original source

* `RECEEXPERTS_ANALYSIS.md:142-148` — event capture at pro-audio priority:

  > `WASAudioCapture::Initialize / InitializeRender`, `WASAPI can't set event handle`, `AVRT.dll: AvSetMmThreadCharacteristicsW`, `WINMM.dll: timeBeginPeriod`, `CreateMutexW`, each source own WASAPI client + thread in shared mode.

* `ere-analysis/decompiled_audio.c:71-81` — reconstructed init:

  ```c
  CoCreateInstance(MMDeviceEnumerator) -> EnumAudioEndpoints
  -> GetMixFormat / GetDevicePeriod
  -> IAudioClient::Initialize(SHARED, EVENTCALLBACK)
  -> CreateEvent + SetEventHandle + GetCaptureClient/GetRenderClient
  -> AvSetMmThreadCharacteristicsW("Pro Audio") + timeBeginPeriod(1)
  -> CaptureThread: WaitForSingleObject(event) -> GetCurrentPadding -> GetBuffer/ReleaseBuffer -> PCMMixer::OnGotPcmData
  ```

* `RECEEXPERTS_ANALYSIS.md:163-174` + `EDGE_CASES.md:23-41` — FIFO absorbs jitter, fixed frames feed encoder:

  > `av_audio_fifo_alloc/_write/_read/_size/_reset`, `remaining > p->frame_size`, `av_buffersrc_add_frame`, `av_buffersink_set_frame_size`. Push variable WASAPI packets, pop fixed `frame_size` (480 = 10 ms). Short famine = silence insert, burst = FIFO grows.

* `RECEEXPERTS_ANALYSIS.md:191-201` — one master clock + watchdog:

  > `LocalRecordGraph::GetCurrentTimeStamp`, `av_gettime`, `av_interleaved_write_frame`, `lagTime` log, `Reset to not use ffmpeg const filter lagTime`, `RestartGraph`, `OnLostDevice`.

* `ere-core/src/graph.cpp:56-57` — pre-roll 2 frames before first mix so frame 0 isn't silence.
* `ere-core/src/graph.cpp:69-91` — master `MasterClock`, `popFull` both FIFOs per frame, `checkLag` watchdog, pace to audio clock.

### 3.2 Ours

* `Hearbud/RecorderEngine.cs:850-976` (`OnLoopbackData`) + `741-840` (`OnMicData`): loopback callback pulls `Math.Min(gotLoop, _micCount)` from `_micRing`, zero-fills shortfall (`931-935`), mixes immediately at whatever `frames = e.ByteCount / BlockAlign` size arrived. No fixed frame size.
* Only mic has a ring; speaker has no FIFO. No per-source FIFOs.
* No MMCSS, no `timeBeginPeriod`, no event-handle setup — relies on CSCore thread defaults.
* No pre-roll; first loopback chunk with empty ring = silence + underrun count.
* `lock (_micRingLock)` wraps multi-sample copy loops in both callbacks.
* Drift is observed (`908-916`: `backlogSec`, `_micBacklogSecMax`, log every N blocks) but never corrected.

### 3.3 Impact

Jitter/drift accumulate. A 0.1–0.5% crystal difference (see `EDGE_CASES.md:15-21`) shows as slowly growing `backlogSec`. Variable chunk mixing causes level wobble and makes AEC (if added later) ineffective, since AEC requires aligned streams.

### 3.4 How to fix

1. **Pin capture timing.** P/Invoke `AvSetMmThreadCharacteristicsW("Pro Audio")` / `AvRevertMmThreadCharacteristics` + `timeBeginPeriod(1)`/`timeEndPeriod(1)` on the threads that own WASAPI clients. Verify CSCore uses `AUDCLNT_STREAMFLAGS_EVENTCALLBACK`; if it exposes only polling, switch to event-driven `IAudioClient` or a CSCore event mode. Reference: `EDGE_CASES.md:80-84`.
2. **Two bounded FIFOs.** Add one FIFO per source (capacity ~1 s, e.g. 48000 floats mono). On overrun drop oldest + count (see `EDGE_CASES.md:29-33`, `ere-core/src/fifo.cpp`). Never grow unbounded.
3. **Fixed-frame mixer tick.** Accumulate to e.g. 480/960/1024 frames, then emit exactly one frame per tick. On underrun emit full frame with zero-pad + `silenced` counter (see `EDGE_CASES.md:35-41`). This replaces "mix whatever arrived."
4. **Master clock + watchdog.** Single `Stopwatch`-based pts (you already have `_tickMs`); per second log `lagTime`-equivalent (FIFO depth + silenced frames). If lag > ~80 ms sustained, drop constant-filter path / resync instead of sliding (see `EDGE_CASES.md:86-91`).
5. **Pre-roll.** Wait until both FIFOs hold ≥2 frames before first mix (see `graph.cpp:56-57`).
6. **Shrink locks.** Lock only around FIFO push/pop indices, never around resample/mix/alloc. Mirror `PCMMixer` "mixer lock" discipline (`decompiled_audio.c:52-69`).

Verify: `backlogSec` stable ~0.02–0.05 s over 10-min 16k-mic + 48k-loopback run; zero `silenced` spikes except intentional gap test; stage dumps (see §10) show aligned outputs.

---

## 4. GAP-02 — Resampling (P1, quality)

### 4.1 Original source

* `RECEEXPERTS_ANALYSIS.md:151-161`: `qAudioResampleConvert::SetWaveFmt`, `AudioResampleConvert::ProcessPcm`, `sampleRate:%d from:%d to:%d`, `swr_alloc_set_opts / swr_convert / swr_get_delay`, `aresample=%d,volume=%f:eval=frame,aformat=...`.
* `EDGE_CASES.md:8-13`: "resample to ONE rate/fmt/layout immediately on arrival, before FIFO."
* `decompiled_audio.c:52-69`: `swr_convert` per channel, `swr_get_delay` so pts stay truthful.

### 4.2 Ours

`Hearbud/RecorderEngine.cs:995-1075` (`ConvertToTarget`):

* Rate: linear interpolation (`1022-1037`: `s0+(s1-s0)*t`).
* Channels: dup mono→stereo, average stereo→mono (`1047-1063`), else nearest-channel copy.
* No delay compensation; `dstFrames = srcFrames * dstRate / srcRate` truncates per chunk, adding ±1-frame jitter per callback.

Audible on typical 16 kHz mono headset → 48 kHz stereo loopback: dull highs, imaging shift, slow drift.

### 4.3 How to fix

1. Resample **on arrival per source** (in `OnMicData` before ring push; loopback is already at `_outRate` so passthrough), not lazily at mix time.
2. Replace linear with proper polyphase/SRC: MediaFoundation resampler DMO, or bundle `libswresample`/`SoXR` via P/Invoke. Keep channel remix separate (proper downmix coefficients, not `0.5*(l+r)` for all cases — check ITU weights if you keep stereo→mono).
3. Track resampler delay (equivalent of `swr_get_delay`) and add to pts/backlog math.
4. Unit test: 16 k→48 k sine sweep, 44.1 k→48 k 1 kHz tone; assert THD+N and length `±1 frame` per second, compare before/after.

---

## 5. GAP-03 — Mix formula + soft clip (P0, bug)

### 5.1 Original source

* `RECEEXPERTS_ANALYSIS.md:176-188`: `PCMMixer::SetMicrophoneVolume/SetSpeakerVolume`, `AudioAmix::Initialize/AddOuputFilter/ApplyVolumeLevel2Graph`, `[in0]volume=%f[in0];[in1]volume=%f[in1];[in0][in1]amix=inputs=2[out]`, `amix=inputs=%d,volume=3dB[out]`.
* `decompiled_audio.c:19-33`: n=1 passthrough, n=2 fast path with per-input volumes, n-input generic path.
* `ere-core/src/mixer.cpp:6-19`: sum, peak-normalize only if `peak>1`, clamp to [-1,1].
* `EDGE_CASES.md:43-49`: per-input `volume:eval=frame` (live slider, no graph rebuild) + amix normalize + clamp; test by setting both volumes to 1.5 and confirming no wrap.

### 5.2 Ours

* Mix: `Hearbud/RecorderEngine.cs:946-950`: `a = (s*LoopGain + m*MicGain)*0.5`, then `SoftClipIfNeeded`.
* Mic-only drive: `821-825`: `a = (m*MicGain)*0.5`.
* `SoftClipIfNeeded` (`1077-1082`): `return MathF.Tanh(x)` on **every** sample, not just overs.

Consequences:

1. `-6 dB` penalty whenever only one side is active (common: speech over silent desktop). Users push gains to 2–3× to compensate, raising noise.
2. Global `tanh` compresses quiet passages and changes timbre even at nominal levels. RecExperts never does this — it normalizes only on overs.

### 5.3 How to fix

1. Change mix to `sum = s*gL + m*gM; if peak>1 scale by 1/peak; clamp`. Apply per-block peak over the emitted fixed frame (see §3.4.3). Keep gains pre-metering as today (documented in `docs/ARCHITECTURE.md:473-481`).
2. Replace unconditional `tanh` with conditional limiter: if `|x|<=1` passthrough; else `sign*tanh` or soft-knee. Or expose limiter on/off.
3. Fix mic-only path to full level, not `*0.5`.
4. Regression test: system-silence + mic 0 dBFS sine → mix peak ≈ 0 dBFS (currently ≈ -6 dBFS). Both-tone 0 dBFS test → no wrap, peak ≤ 0 dBFS.

---

## 6. GAP-04 — Process isolation (P2, robustness)

### 6.1 Original source

* `RECEEXPERTS_ANALYSIS.md:116-117`: `RecordExecutor.exe` = out-of-proc record host, gRPC `ERE.Record/*` (`CtrlRecordState/RefreshDevice`, `GetFileMicrophoneMute/SpeakerMute`). UI crash doesn't kill capture.

### 6.2 Ours

Single process. `CrashLog.LogAndShow` in `App.xaml.cs` is last-resort; any unhandled UI exception ends capture.

### 6.3 How to fix

Short term (keep single process): harden `RecorderEngine.Dispose` idempotency + write-queue drain + header finalize on every exit path (you already drain in `StopAsync` — extend to `HandleDeviceDisconnectedAsync` and crash handler).

Long term (if hour-long captures matter): split capture to child process or Windows service with IPC (named pipes/gRPC), UI as controller. Do not copy the full `ERE.Record` RPC surface — minimal `Start/Stop/Pause/SetVolume/Mute` + progress is enough. Cost/benefit should be weighed; single-process is acceptable for short captures.

---

## 7. GAP-05 — AEC / AGC / noise suppression (P2, meetings)

### 7.1 Original source

* `RECEEXPERTS_ANALYSIS.md:189`: `WinAudioAecDmo::Start`, `DmoMediaBuffer`, `WebRTCWrapper`, `m_agc:%p`.
* `EDGE_CASES.md:58-64`: "AEC needs BOTH streams time-aligned (hence sync first), then subtracts delayed speaker estimate from mic; AGC/NS after." Stub: `cancelEcho(a,b)` in `ere-core/src/aec.cpp`, invoked in `graph.cpp:78`.
* Debug: `webrtc_out.pcm`, `ns_2C16b48000.pcm`.

### 7.2 Ours

None. Speaker bleed into mic is mixed twice.

### 7.3 How to fix

1. Do §3 + §4 first — AEC on misaligned streams smears.
2. Add optional (default off) AEC: WebRTC AECM via P/Invoke or `WinAudioAecDmo`, then AGC/NS. Insert between per-source FIFO pop and mix (same place as `graph.cpp:78`).
3. Expose per-input mute/volume as live per-frame gains (you already have runtime-adjustable gains — keep that) plus AEC toggle. Reference RPC names if you mirror API: `SetFileMicrophoneMute/SpeakerMute`, `SetRecordVolume` (`RECEEXPERTS_ANALYSIS.md:90-97`).

---

## 8. GAP-06 — Device hot-swap (P1, robustness)

### 8.1 Original source

* `EDGE_CASES.md:69-76`: `LocalRecordGraph::OnLostDevice/RestartGraph/ChangeAudioDeviceInRecording`, `NotifyAudioDeviceChange::OnDeviceStateChanged/PushAudioDevice`, `EnumDevice::RefreshDevice`, `/ERE.Record/ChangeAudioDevice`. Rule: detect → stop that WASAPI client → keep mux alive on silence → re-enumerate → re-init resampler/FIFO → continue file. Plus `FileMuxer::DealFfmpegMuxError`, `FixupFile::Useffmpeg2Reload`, `av_write_trailer`.
* `RECEEXPERTS_ANALYSIS.md:191-201`: `OnLostDevice / RestartGraph`, not a dead file.

### 8.2 Ours

* `MainWindow.xaml.cs:595-645` (`IMMNotificationClient`): `OnDeviceAdded` refreshes + restarts monitor; `OnDeviceRemoved` → `HandleDeviceDisconnectedAsync` then refresh. Good for UI/monitoring.
* But in-recording loss: `RecorderEngine.cs:842-846,970-974` catches `CoreAudioAPIException HResult 0x88890004` → `RaiseStatus(Error, "Audio device disconnected")` + `StopInternal(fullStop:true)`. Recording ends; user must start a new file.

### 8.3 How to fix

1. On loss during `_recording`: keep `_writeQueue`/WAVs open, feed silence frames to mix (same underrun path as §3.4.3), set per-source "lost" flag.
2. Re-enumerate (reuse `SafeRefreshDevices` logic headlessly), re-open that source, re-init its resampler/FIFO for the new mix format if changed, resume pushing.
3. Always finalize headers/trailer even on abort (WAV header rewrite; future compressed mux: `av_write_trailer` equivalent). Add startup "repair unfinalized files" scan (cf. `gRepairVideoObject.isNeedFixup/startFixup`, `BACKEND_API.md:72-73`).

---

## 9. GAP-07 — Formats, mux, and I/O (P2, efficiency)

### 9.1 Original source

Real app encodes AAC into MP4/MKV (`aac_coder-twoloop`, `avformat_*`), interleaved by pts (`av_interleaved_write_frame`). Post: `EreConverter.dll` trim/compress/concat, intro/outro, SRT→ASS (`RECEEXPERTS_ANALYSIS.md:122`).

### 9.2 Ours

3x parallel WAVs + post MP3 re-read (`docs/ARCHITECTURE.md:296-322`). 3x write I/O during capture; MP3 pass re-reads full mix.

### 9.3 How to fix

1. Make stems optional: default mix-only; checkbox for `*-system/-mic` debug stems. Cuts I/O 3×→1× for most users.
2. Optionally encode compressed directly (AAC/MP3 streaming sink) instead of WAV→MP3 post pass, keeping WAV only when "Original" selected.
3. Keep MP3-failure-still-leaves-WAVs behavior (documented in `docs/ARCHITECTURE.md:460`).
4. Do not build a video converter/player — out of scope (see §12).

---

## 10. GAP-08 — Diagnostics (P1, observability)

### 10.1 Original source

Per-stage taps + per-second watchdog: `microphoneIn.pcm / speakerIn.pcm / mixOut.pcm / webrtc_out.pcm`, `%s seconds:%d capture:%d convert:[%.03f]ms encode:%d time:[%.03f]ms v:%.03f a:%.03f lagTime:%.03f` (`RECEEXPERTS_ANALYSIS.md:191-201`). `ere-core` mirrors with `--dump-dir` + `--drift/--gap/--unplug` chaos flags (`ere-core/README.md:21-22`).

### 10.2 Ours

Good start: session `*.txt` log, `Mic ring backlog: X s (max Y s)` every N blocks, `_micUnderrunBlocks`, `_micBacklogSecMax`, 10 MB log cap (`RecorderEngine.cs` header `21-27`, `docs/ARCHITECTURE.md:454-463`).

Missing: per-stage dumps, overrun counter, lag watchdog line, resampler-delay visibility.

### 10.3 How to fix

1. Add `--dump-dir`-equivalent (hidden setting): write `microphoneIn/speakerIn/mixOut` float dumps when enabled. Invaluable for "why did it click?" reports.
2. Log overruns (drop-oldest events) alongside underruns; log `lagTime`-equivalent per second (FIFO depths + silenced frames + resampler delay).
3. Add chaos-test harness: synthetic mic drift/gap/unplug/mute (cf. `ere-core` `--drift/--gap/--unplug/--mute-mic-at`, `learn_audio_sync_edge.cpp`) to CI (`Hearbud.Tests/`).

---

## 11. GAP-09 — UX / feature parity (P3, in priority order)

Derived from `ere-ui/orig/qml/**` + `ere-ui/runner/BACKEND_API.md`.

| # | RecExperts feature | QML/backend evidence | Hearbud today | Fix |
|---|---|---|---|---|
| 1 | Pause/resume | `gRecordScreenObject.pauseRecord/restartRecord/isPause`, `ETrayTypePause` (`BACKEND_API.md:69,101`) | Stop only | Add pause (keep files open, feed silence, keep clock) |
| 2 | Mic/speaker enable + mute toggles, at-least-one guard | `microphoneEnable/speakerEnable/microphoneId/speakerId`, `if(!mic && !spk)`, `disableTips: "Keep sound or mic on"` (`RECEEXPERTS_ANALYSIS.md:80-88`), `SetFileMicrophoneMute/SpeakerMute` | `IncludeMic` bool only, gains 0–3 | Two toggles + mutes; disable Start unless ≥1 on |
| 3 | Per-device volume dB + sound test | `DeviceModel.getDeviceVolumeDb/setDeviceVolume`, `SoundMicrophoneTest`, `TestSoundMicrophone/`, `SoundTest.wav` | Gains affect meter+mix, no dB, no test | dB labels + test-tone/mic-loopback button |
| 4 | Countdown + floating toolbar + tray states | `ESetCountdown/ESetEnableCountdown`, `RecordBar/RecordingBar/`, `trayType`, `showTrayMiniAnimation` | Basic tray, no countdown | 3-2-1 + mini bar + `Recording` tray icon |
| 5 | Auto-stop / auto-split | `gEreSettings.autoStop*/autoSplit*`, `checkAutoStop` (`BACKEND_API.md:59`) | None | Time/size stop + split (new file, no gap) |
| 6 | Hotkeys | `gEreHotKey.{start,pause,snapshot,annotate}Hotkey`, `registerAllHotKey` | Hardcoded Ctrl+R/S | Customizable hotkeys |
| 7 | Disk-space / permission / NTFS guards | `checkOutputPrmission/checkeOutputFreeSpace/isNtfsFileSystem`, `LockDiskSpace`, `EOperNotEnoughSpace` | None (queue-full drop only) | Pre-flight free-space + mid-record low-space warning |
| 8 | Recording list + repair + player | `gEreMultimediaObject`, `gRepairVideoObject`, `PlayerMainPage.qml` | Files on disk only | History list + fixup scan + play-in-app |
| 9 | Output format/rate/quality picker | `ESetAudioBitrate/ESetAudioFormat/ESetAudioSampleing`, `supportAudioFormats` | MP3 bitrate only | Sample-rate/format picker when stems on |

---

## 12. Explicitly out of scope (do not clone)

* Screen/window/rect/game capture, virtual-monitor driver (`VirtualMonitor.*`, `devcon.exe`), WinRT capture.
* Camera PiP + AI background matting (`onnxruntime`, `ImageFilter.dll`).
* Video editor (trim/concat/intro/outro/watermark/cursor effects), screenshot tool.
* Speech-to-text / summary / translation / subtitles (`Whisper_license`, `Subtitle*`, `AiSummary*`).
* Licensing/purchase/activation (`EreActivation`, `InnerBuy/`), updater/remote-config/analytics (`update.easeus.com`, `SensorsSender.exe`, `AliyunWrap`).
* Full `EreQmlEnum` (129 values), `DeviceModel`, `Player*`, `Converter*` backend — see `BACKEND_API.md:103-121` for scale.

Hearbud's advantage is staying a small, trustworthy audio tool. Match RecExperts' **audio core discipline**, not its suite breadth.

---

## 13. Recommended fix order

### P0 — do first (audible bugs)

1. **GAP-03 mix/normalize + limiter** (§5) — 1-file change, biggest audible fix.
2. **GAP-01 sync core** (§3) — FIFOs + fixed frames + MMCSS + pre-roll + lock discipline.

### P1 — do next (quality + resilience)

3. **GAP-02 resampler** (§4).
4. **GAP-06 hot-swap continue** (§8).
5. **GAP-08 diagnostics** (§10) — dumps + overrun/lag logging + chaos tests.

### P2 — when P0/P1 solid

6. **GAP-07 I/O** (§9) — optional stems, streaming encode.
7. **GAP-05 AEC/AGC/NS** (§7) — optional, post-alignment.
8. **GAP-04 isolation** (§6) — only if long-capture reliability demands it.

### P3 — UX

9. **GAP-09 items 1–7** (§11) in table order.

---

## 14. Acceptance checklist (per fix)

* **Mix:** single-source 0 dBFS sine → mix ≈ 0 dBFS; dual 0 dBFS → no wrap, peak ≤ 0 dBFS; quiet passage unchanged with limiter on.
* **Sync:** 10-min 16 kHz-mic + 48 kHz-loopback: backlog stable, no growth; gap test (3-tick dropout) → silence, no tear; drift 0.5% → absorbed, lag watchdog fires, no crash.
* **Resample:** sweep THD+N within ~1 dB of `swr` reference; length ±1 frame/s.
* **Hot-swap:** unplug mic mid-record → file continues, silence bridged, new device resumes same file, header valid.
* **Diagnostics:** dump-dir reproduces reported glitch offline; overrun/underrun/lag counters in session log.

---

## 15. Key file pointers (for implementers)

**Original:**

* `RECEEXPERTS_ANALYSIS.md:76-98` — mic+speaker proof + engine classes.
* `RECEEXPERTS_ANALYSIS.md:136-201` — 5-layer sync explanation.
* `ere-analysis/decompiled_audio.c:1-81` — `AudioAmix`, `PCMMixer`, `WASAudioCapture` pseudocode.
* `ere-analysis/EDGE_CASES.md:1-92` — per-case binary refs + toy knobs.
* `ere-analysis/LEARN_AUDIO_SYNC.md:1-30` — 5-idea map.
* `ere-core/src/graph.cpp:29-97`, `mixer.cpp:6-19`, `README.md:1-25`.
* `ere-ui/runner/BACKEND_API.md:13-121` — context props, `gRecordScreenObject`, `DeviceModel`, enums.
* `ere-ui/orig/qml/RecordingMainPage.qml`, `RecordBar/`, `RecordingBar/`, `TestSoundMicrophone/`, `EreSettings/`.

**Ours:**

* `Hearbud/RecorderEngine.cs:29-35` — clock design intent.
* `Hearbud/RecorderEngine.cs:741-840` — `OnMicData` + mic-only drive (`818-825`).
* `Hearbud/RecorderEngine.cs:850-976` — `OnLoopbackData` + mix (`943-951`).
* `Hearbud/RecorderEngine.cs:995-1075` — `ConvertToTarget` (linear).
* `Hearbud/RecorderEngine.cs:1077-1082` — `SoftClipIfNeeded` (global tanh).
* `Hearbud/RecorderEngine.cs:842-846,970-974` — device-lost stop.
* `Hearbud/MainWindow.xaml.cs:595-645` — `IMMNotificationClient`.
* `docs/ARCHITECTURE.md:328-379,454-497` — sync, edge cases, limitations.
