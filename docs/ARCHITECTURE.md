# Hearbud Architecture Overview

## Table of Contents
- [Project Purpose](#project-purpose)
- [Technology Stack](#technology-stack)
- [High-Level Architecture](#high-level-architecture)
- [Core Components](#core-components)
- [Data Flow](#data-flow)
- [Design Decisions](#design-decisions)
- [Performance Optimizations](#performance-optimizations)

---

## Project Purpose

Hearbud is a Windows desktop application that captures **system audio (WASAPI loopback)** and **microphone** simultaneously in real-time. It's designed for meetings, streams, and quick captures where you need both the computer's output and your voice recorded together.

The app produces three files:
1. `<base>-system.wav` - Raw system/loopback audio (16-bit PCM)
2. `<base>-mic.wav` - Raw microphone audio (16-bit PCM, converted to match loopback format)
3. `<base>-mix.wav` - Mixed audio with configurable gains and soft clipping (32-bit PCM by default)
4. Optional: `<base>.mp3` - Encoded MP3 version of the mix

---

## Technology Stack

### Framework & Language
- **.NET 8.0** targeting Windows
- **C# 12** with nullable reference types enabled
- **WPF (Windows Presentation Foundation)** for UI
- **Windows Forms** interop for folder browser dialog

### Key Dependencies
- **CSCore** (v1.2.1.2) - Core audio processing library
  - WASAPI loopback/capture APIs
  - Wave file I/O
  - Media Foundation integration for MP3 encoding
- **H.NotifyIcon.Wpf** (v2.3.0) - System tray icon support
- **System.Text.Json** (v8.0.5) - Settings serialization

### Audio APIs
- **WASAPI (Windows Audio Session API)** - Primary audio capture mechanism
- **Media Foundation** - MP3 encoding via CSCore wrapper

---

## High-Level Architecture

```
┌─────────────────────────────────────────────────────────────┐
│              MainWindow (WPF) + Settings dialog             │
│  - Device selection, gain controls, basic output            │
│    (format/limits/countdown/hotkeys in Settings…)           │
│  - UI level meters via DispatcherTimer (100ms, UI thread)   │
└──────────────────────┬──────────────────────────────────────┘
                       │
                       │ Events: LevelChanged, Status, EncodingProgress
                       │
                       ▼
┌─────────────────────────────────────────────────────────────┐
│                   RecorderEngine (Core)                     │
│  - Audio capture (loopback + mic)                           │
│  - Real-time mixing and DSP                                 │
│  - Async disk I/O (background thread)                       │
│  - Ring buffer synchronization                              │
└──────────────────────┬──────────────────────────────────────┘
                       │
                       │ WASAPI Callbacks (OnLoopbackData, OnMicData)
               ┌───────┴────────┐
               │                │
               ▼                ▼
┌──────────────────┐  ┌──────────────────┐
│ Loopback Capture │  │  Mic Capture     │
│ (WasapiLoopback  │  │ (WasapiCapture)  │
│  Capture)        │  │                  │
└──────────────────┘  └──────────────────┘
```

### Threading Model

1. **UI Thread** - WPF main thread, handles user input and `DispatcherTimer` meter updates (100ms interval, no cross-thread invoke in the tick path)
2. **Audio Threads** - CSCore-managed threads calling `DataAvailable` callbacks (~100-150 times/sec)
3. **Disk Writer Thread** - Background thread consuming from `BlockingCollection< AudioWriteJob>` queue
4. **MP3 Encoding Thread** - Thread pool task during post-processing

**Critical Design Rule:** Never block the audio callback threads. All disk I/O is offloaded to the background writer thread.

---

## Core Components

### 1. App / App.xaml.cs
**Purpose:** Application entry point and global exception handling

**Key Responsibilities:**
- Global crash handlers (AppDomain unhandled exceptions, dispatcher unhandled exceptions, task scheduler unobserved exceptions)
- Crash reporting via dialog + debugger output (no log file)

**Important Notes:**
- `CrashLog.LogAndShow()` is the single point of failure reporting
- All exceptions are caught to provide user-friendly crash messages before app termination

### 2. MainWindow / MainWindow.xaml + .xaml.cs
**Purpose:** Main UI window providing controls and feedback

**Key Responsibilities:**
- Device enumeration and selection (mic + speaker) with include checkboxes (unchecked = soft-mute + disabled combo)
- Gain sliders (independent mic and system gains) and real-time level meters (RMS + Peak with clip indication) in one Audio group
- Basic output (save location, base filename); transport (Start/Pause/Stop) in the footer with elapsed time while recording
- Status messages, countdown cancel, and encoding progress in the footer
- Keyboard shortcuts: Ctrl+R (start), Ctrl+P (pause/resume), Ctrl+S (stop) — remappable in Settings
- System tray icon integration (Pause/Stop items follow the same enable rule as the buttons; balloon tips for auto-stop/split and USB warnings)

**UI State Management:**
- `_uiReady` flag guards UI updates during initialization
- `_engine` (RecorderEngine) instance lives for window lifetime
- Device dictionaries (`_micDict`, `_spkDict`) hold `MMDevice` objects for quick access; refresh disposes prior instances before clearing
- Window auto-sizes to content (`SizeToContent="Height"`), opens centered (`CenterScreen`)

**Timer Behavior:**
- 100ms `DispatcherTimer` (`_uiTimer`, UI thread) updates meters and Pause/Stop enable state; brushes resolved once, no per-tick invoke
- 1s `DispatcherTimer` (`_limitTimer`) polls auto-stop/split limits while recording

### 3. RecorderEngine
**Purpose:** Core audio processing engine handling capture, mixing, and output

**Class Structure:**
```csharp
public sealed class RecorderEngine : IDisposable
```

#### State Machine
- `_monitoring` - Audio devices open, meters active, not recording
- `_recording` - Actively writing to disk
- `_disposed` - Cleanup complete

#### Key Internal State
| Field | Purpose | Notes |
|-------|---------|-------|
| `_loopCap`, `_micCap` | WASAPI capture instances | `WasapiLoopbackCapture` / `WasapiCapture` |
| `_loopIn`, `_micIn` | CSCore `SoundInSource` wrappers | FillWithZeros = true for safety |
| `_wavSys`, `_wavMic`, `_wavMix` | Wave file writers | Managed by separate thread |
| `_writeQueue`, `_writeTask` | Async I/O queue | BlockingCollection with capacity 2000 |
| `_micRing` | Mic audio ring buffer | Synchronized via `_micRingLock` |
| `_micGain`, `_loopGain` | Runtime-adjustable gains | Applied before mixing and metering |
| `_outRate`, `_outChannels` | Output format | Matches loopback device |

#### Public API Methods

##### `MonitorAsync(RecorderStartOptions opts)`
- Full restart: tears down devices, reopens from `opts`, restarts streams (no file output)
- Activates level metering
- Used by refresh/auto-monitor/start paths — not by the include checkboxes

##### `MonitorSourcesAsync(RecorderStartOptions opts)`
- Idle re-target used by the include checkboxes: applies software mute (`SetChannelMute`) for unchecked sources, opens a wanted side only when it is missing — never stops/restarts a running capture

##### `StartAsync(RecorderStartOptions opts)`
- Calls `MonitorAsync()` first (ensures devices open), clears idle mutes
- Generates unique output file paths (`UniquePath`: `rec (1).wav`, `rec (2).wav`, …)
- Opens mix + optional stems (3 WAVs when `WriteStems`, else mix only) and 1 session log file
- Initializes async write queue and background writer thread
- Resets mic ring buffer and counters

##### `StopAsync()`
- Sets `_recording = false`
- Drains write queue: calls `_writeQueue.CompleteAdding()` and awaits `_writeTask`
- **Critical:** WAV headers are finalized on `Dispose` after queue drains
- Encodes MP3 (post-process) on thread pool thread
- Closes session log file

##### `Dispose()`
- Disposes all capture devices and file writers
- Aborts write queue if still running
- Full cleanup for app shutdown

### 4. AppSettings
**Purpose:** Persistent application settings

**Storage:** `%UserProfile%\.hearbud_config.json`

**Key Properties:**
- `MicName`, `SpeakerName` - Last selected devices
- `OutputDir` - Default save folder
- `Mp3BitrateKbps` - MP3 encoding bitrate (0 = Original/WAV only)
- `MicGain`, `LoopGain` - Persisted gain values
- `IncludeMic` - Whether to include mic (unchecked → software-mute while idle; excluded from the recording device set)
- `IncludeSystem` - Whether to include system audio (same soft-mute/exclude behavior; at-least-one required: "Keep system audio or microphone on to record.")
- `WriteStems` - Save separate mic/system stems (false = mix only, ~1x I/O)

**Legacy Handling:**
- Old configs used `Mp3Quality` string (e.g., "192 kbps")
- Migration logic parses numeric value from legacy string
- When loading new configs lacking `Mp3BitrateKbps`, regex extracts bitrate from `Mp3Quality`

### 5. Dbfs (Static Utility)
**Purpose:** Audio level calculations and formatting

**Key Methods:**
- `FormatGain(double value)` - Converts linear gain multiplier to string (e.g., "2.00× (+6.0 dB)", "0.00× (-∞ dB)" for zero)
- `ToDbfs(double peakLin)` - Converts linear peak to dBFS clamped to -60 dB floor

### 6. SettingsWindow / SettingsWindow.xaml + .xaml.cs
**Purpose:** Modal dialog for everything that left the main window: output format + MP3 bitrate, stems toggle, auto-stop/split limits, pre-record countdown, remappable hotkeys

**Key Responsibilities:**
- Edits the live `AppSettings` in place (hotkeys apply + save immediately on capture); OK persists the rest (`AppSettings.Save()`) and the main window rebuilds input bindings; Cancel restores the snapshot taken at open
- Hotkey capture boxes + reset-to-defaults live here (window-focused scope only)

### 7. UsbTopology (UsbTopologyProbe)
**Purpose:** Best-effort detection of a mic and mouse sharing one non-root USB hub (a topology observed to swallow clicks before Windows sees them)

**Key Responsibilities:**
- `Check(endpointId, friendlyName)` runs once at startup (pool thread, after the window is up) and again from Refresh Devices; any native/COM failure means "no warning", never an error
- On a shared hub: inline warning banner in the Devices group + mic tooltip + one tray balloon, logged via `CrashLog` (`UiDiagnostics.Usb`)
- `FindSharedHub` is pure topology math over parent/service maps (unit-tested); `Describe` dumps the resolved hub chains; the probe also reports USB selective-suspend state

### 8. UiDiagnostics
**Purpose:** Small UI-health helpers whose output goes to the debugger via `CrashLog` (no log file)

**Key Members:**
- `BuildEnvReport` - one-shot environment report (OS, render tier, remote/tablet/DPI) at startup
- `InputEventRing` / `LagTracker` - recent input events and UI-thread lag stats, summarised at close
- MainWindow adds first-frame/close timings and a render-thread frame counter around these

---

## Data Flow

### Audio Capture Flow

```
┌─────────────────────────────────────────────────────────────────────────┐
│                          Audio Callback Thread                          │
│                         (WASAPI-managed)                                │
└─────────────────────────┬───────────────────────────────────────────────┘
                          │
                          │ DataAvailableEventArgs (PCM bytes)
                          │
                          ▼
                 ┌───────────────────┐
                 │ ReadExactSamples  │  ← Converts PCM to float samples
                 │       ↓           │
                 │   float[] buffer  │
                 └─────────┬─────────┘
                           │
                           │ [Mic only path]
                           │
                    ┌──────┴───────┐
                    │ ConvertToTarget │  ← Resample/remix to match loopback format
                    │       ↓        │
                    │ _micConvBuf[]   │
                    └──────┬─────────┘
                           │
                 ┌─────────▼─────────┐
                 │ Mic Ring Buffer    │  ← Synchronized ring buffer
                 │ (_micRing[])       │
                 └─────────┬──────────┘
                           │
          ┌────────────────┼────────────────┐
          │                │                │
          ▼                ▼                ▼
    ┌──────────┐    ┌──────────┐    ┌──────────┐
    │ Metering │    │ Write Mic │    │ Queue to │
    │ (accum)  │    │ WAV       │    │ Writer   │
    └──────────┘    └──────────┘    └──────────┘
                           │
          [Loopback path]  │         [Mixing path]
                           │
          ┌────────────────▼────────────────┐
          │     OnLoopbackData callback     │
          │  (on same or separate thread)   │
          └────────────────┬────────────────┘
                           │
                  ┌────────▼────────┐
                  │ Read loopback   │
                  │ to float[]      │
                  └────────┬────────┘
                           │
                 ┌─────────▼──────────┐
                 │ Pull same-sized    │  ← Clock source synchronization
                 │ chunk from ring    │
                 │ (zero-fill if late) │
                 └─────────┬──────────┘
                           │
                 ┌─────────▼──────────┐
                 │ Mix: s + m          │  ← Gains pre-mix
                 │ scale iff peak>1    │
                 │ + SoftClipIfNeeded  │
                 └─────────┬──────────┘
                           │
          ┌────────────────┼────────────────┐
          ▼                ▼                ▼
    ┌──────────┐    ┌──────────┐    ┌──────────┐
    │ Write Sys│    │ Write Mix│    │ Queue to │
    │ WAV      │    │ WAV      │    │ Writer   │
    └──────────┘    └──────────┘    └──────────┘
```

### Async Disk I/O Flow

```
┌──────────────────────┐         ┌────────────────────────────────┐
│ Audio Callbacks      │         │  Background Writer Thread      │
│ (OnLoopbackData,     │         │  (DiskWriteLoop method)        │
│  OnMicData)          │         └─────────────┬──────────────────┘
│                      │                       │
│ EnqueueWrite()       │   BlockingCollection │   For each job:
│ + ArrayPool.Rent()   │◄──────────────────────────────────────────���►
│ + Array.Copy()       │    _writeQueue        │   Switch(Target)
│   _writeQueue.Add()  │                       │     wavSys?.Write()
│                      │   (Capacity: 2000)    │     wavMic?.Write()
│ [Non-blocking unless │   (~5 sec buffer)     │     wavMix?.Write()
│  queue full]         │                       │   Finally:
│                      │                       │     ArrayPool.Return()
└──────────────────────┘                       └──────────────────────┘
```

**Key Points:**
- `BlockingCollection` blocks producer if queue is full (rare, only on extreme disk stalls)
- `_writeQueue.CompleteAdding()` signals end of data during stop
- `ArrayPool<byte>.Shared` recycles buffers, zero per-block allocation overhead

### MP3 Encoding Flow (Post-Process)

```
StopAsync() called
          │
          ▼
┌─────────────────────┐
│ Drain write queue   │  ← Await _writeTask completion
│ Close WAV files     │  ← Headers finalized on Dispose
└─────────────────────┘
          │
          ▼
   if Mp3BitrateKbps > 0
          │
          ▼
┌─────────────────────┐   Task.Run on thread pool
│ Task.Run {          │◄────────────────────────────────►
│   WaveFileReader   │   Read mix.wav in chunks (64KB)
│   Convert to 16-bit│   (if mix is 32-bit)
│   MediaFoundation  │   encoder.Write(chunk)
│   encoder.Write()  │   Report progress via event
│ }                  │
└─────────────────────┘
          │
          ▼
   RaiseStatus(Stopped)
```

---

## Design Decisions

### 1. Loopback as Clock Source
**Decision:** System/loopback audio is the timing master. Mic audio is buffered and pulled to match loopback chunks.

**Rationale:**
- Keeps system and mic perfectly synced in the mix
- Prevents "lining up at start" timing errors
- Mimics what happens in reality: mic hears what the speaker outputs

**Implications:**
- If mic is late, gaps are zero-filled (silence preserved)
- If loopback goes silent, mic can drive output to support mic-only sessions
- **Watch out:** Stale mic data is dropped when loopback resumes, preventing echo artifacts

### 2. WAV Files (stems optional, mix always)
**Decision:** Default `*-system.wav` + `*-mic.wav` + `*-mix.wav`; `RecorderStartOptions.WriteStems=false` writes mix only (1x I/O).

**Rationale:**
- Users can later remix or edit individual tracks in DAW/audacity
- System/mic WAVs are 16-bit (standard, widely compatible)
- Mix WAV is 32-bit (headroom for before MP3 encoding)
- Avoids lossy capture step even if final output is MP3

### 3. Async I/O with BlockingCollection
**Decision:** All disk writes happen on a background thread via a job queue.

**Rationale:**
- Audio callback threads must never block (WASAPI is unforgiving)
- Disk latency spikes (anti-virus scans, network drives, etc.) could cause glitches
- Producer-consumer pattern cleanly separates concerns

**Capacity Choice (2000 jobs):**
- At 48kHz stereo, ~5 seconds of audio buffering
- Queue full only during catastrophic disk failures (not typical)

### 4. Buffer Pooling with ArrayPool
**Decision:** Use `ArrayPool<byte>.Shared` to manage per-block buffers for disk writes.

**Rationale:**
- Zero allocation per audio block (no GC pressure)
- Previously used `new byte[]` 100+ times/sec = significant heap churn
- Pooling is a standard .NET optimization for high-frequency buffers
- **Memory Safety:** `EnqueueWrite` uses a `try/finally` block to ensure rented buffers are always returned to the pool, preventing memory leaks if adding to the queue fails or an exception occurs.

### 5. Dual-FIFO fixed-frame tick + master-clock resync (GAP-01/02/08)
**Decision:** Mic goes through stateful `ConvertToTargetChunked` (phase carry, no seam clicks,
cumulative length +-1 frame/s, ITU fold with LFE excluded) into a bounded mic FIFO (~2 s cap,
drop-oldest + `_micOverrunBlocks`). Loopback also buffers into a bounded loop FIFO
(`_loopOverrunBlocks`); the mix emits fixed 480-frame quanta (960 floats stereo) via
`TryPopFixedFrame`/`EmitFixedMix`. Mic-only sessions drain fixed frames via `EmitMicOnlyFixed`.
Master clock = `_masterFramesEmitted`; sustained backlog >80 ms over 3 ticks triggers
`CheckLagResync` (drop mic to one frame, `_resyncCount`, Warn); 1 Hz `lagTime` line logs
depth/over/loopOver/under/silenced/max/masterFrames/resyncs. Pre-roll 2 fixed frames;
MMCSS Pro Audio on writer thread + 1 ms timer; `DumpDir` taps; log cap keeps WARN/ERROR + tail.

**Rationale:**
- Handles mic/loopback sample rate differences on arrival (not lazily at mix)
- Bounded FIFOs absorb WASAPI jitter/bursts; fixed emit keeps mix clock stable
- Resync bounds drift instead of growing backlog; counters make drift visible
- Pre-roll avoids frame-0 silence + spurious underrun

**Sync Edge Case:** When loopback resumes after silence, mic backlog beyond one fixed frame is dropped on sustained >80 ms lag. This prevents "old mic over new system" echo effect.

### 5b. Pause, source toggles, disk guard (GAP-09 p1-p4)
**Decision:** `Pause()`/`Resume()`/`IsPaused` keep files open and emit silence frames (timeline preserved, meters frozen, resync skipped, loop clock re-armed on resume). Source checkboxes soft-mute while idle (`OnIncludeSourceChanged` → `MonitorSourcesAsync` → `SetChannelMute`, no WASAPI restart; toggles are skipped while recording since sources are fixed for the running file); Start builds the recording device set from the checkboxes and requires at-least-one ("Keep system audio or microphone on to record."). Loopback is optional in `OpenDevicesAsync` (mic drives clock, format derived from mic). Pre-start `CheckDiskSpaceAndWritable` probes folder + zero-byte write, warns <500 MB and blocks <100 MB.

### 5c. AEC/AGC/NS intentionally omitted (GAP-05 accepted gap)
**Decision:** No echo canceller, AGC, or noise suppressor in the capture path. Rationale: this tool records both sides for later listening (no live-call feedback loop); both stems exist so speaker bleed is fixable in post; a loopback reference lags the true render signal (OS mixer + silence gating), making delay estimation fragile; native WebRTC-APM interop (x64+arm64, single-file publish, AV friction) is unjustified without user evidence.
Insertion point is safe if ever needed: `TryPopFixedFrame` returns two aligned 480-frame quanta (the `graph.cpp:78` equivalent) — AEC would hook there, AGC/NS in `EmitFixedMix`/`EmitMicOnlyFixed` after per-input gains. Dump taps already mirror stage taps. Guidance: use headphones for live monitoring.
### 5d. Out-of-process capture deferred (GAP-04)
**Decision:** Capture stays in-process. Rationale: no evidence of long-capture reliability demand; hot-swap (GAP-06) + bounded FIFOs + 30 s drain timeout + log cap already cover the observed failure modes. Revisit only if crash/lockup reports implicate the capture process.

### 5e. Limits, hotkeys, countdown, settings dialog (GAP-09 p5-p9)
**Decision:** Time/size auto-stop/split (Settings dialog: `LimitMinutesText`/`LimitSizeMbText`, `SplitInsteadOfStopCheck`): engine `TotalMixBytesWritten`/`RecordElapsed` getters, 1 s `_limitTimer` poll, split = stop + `(N)` restart.
Hotkeys: `Start/Pause/StopHotkey` settings (`Ctrl+R/P/S` defaults, converter-validated), capture boxes + reset in the Settings dialog, `RebuildInputBindings` on startup/change.
Countdown + in-window transport + tray: `CountdownSeconds` (0/3/5/10, cancellable via footer Cancel) runs before guard/engine start; elapsed time and Pause/Stop stay in the main window footer (no separate mini-bar window); tray Pause/Stop items + balloon tips (no icon swap: single ico).
Format picker (#9 mapping-only, Settings dialog): `OutputFormatCombo` (WAV master / WAV + MP3) + bitrate-only combo map to the same `Mp3BitrateKbps` (0 vs kbps); no engine change, no sample-rate override by design (loopback stays bit-exact).
Removed in the compact-window round: the mic/system sound-test buttons, the recent-sessions history list, and the startup WAV repair scan. `HistoryStore` (JSON log), `SessionManifest` (crash bookkeeping — engine still calls `Begin`/`MarkClosed`) and `WavRepair` (header fix + `ScanUnclosed`/`RepairUnclosed`) remain in the tree with test coverage, but no UI currently writes history or runs the recovery scan.

### 6. Conditional limiter (tanh only on overs) + peak normalize
**Decision:** Sum `s*gL + m*gM` at full level, peak-normalize the block only if peak>1, then apply `tanh` soft-knee only to samples with `|x|>1` before clamp.

**Rationale:**
- Tanh provides natural limiting curve (sounds better than hard clip)
- Prevents harsh digital distortion when gains are pushed
- Still clamps to [-1, 1] final range for PCM conversion

### 7. TPDF Dither on 16-bit Conversion
**Decision:** Add triangular probability density function (TPDF) dither when converting float to 16-bit PCM.

**Rationale:**
- Reduces quantization noise (low-level "crunch" on quiet passages)
- Two random samples subtracted creates triangular distribution (~-1.0 to +1.0)
- Applied before rounding, results in more natural decay tails

**Note:** Only applied to system/mic 16-bit WAVs, not 32-bit mix.

### 8. 32-bit Mix for MP3 Quality
**Decision:** Mix WAV is written as 32-bit PCM (configurable).

**Rationale:**
- Avoids extra 16-bit quantization step before MP3 encoding
- MP3 encoder's own quantization (psychoacoustic) is the only loss
- 16-bit → MP3 would have two quantization steps, more perceptible

---

## Performance Optimizations

### Implemented Optimizations

| Optimization | Location | Impact |
|-------------|----------|--------|
| **Async I/O queue** | `DiskWriteLoop()`, `EnqueueWrite()` | Eliminates audio glitches from disk stalls |
| **Buffer pooling** | `EnqueueWrite()`, `ArrayPool` | Zero GC pressure on per-block allocations |
| **Reusable resampling scratch buffer** | `ConvertToTarget()` ref scratch | No allocations per resampling operation |
| **Throttled level updates** | `_lastLevelTick*` fields | ~50ms throttle reduces UI cross-thread calls |
| **Block-sized ring buffer** | `_micRing` with power-of-2 sizing | O(1) ring operations, efficient read/write |

### Memory Management

- **Pre-allocated buffers:** All significant float/byte arrays are pre-allocated and resized only when needed via `EnsureCapacity()` (power-of-2 sizing)
- **No per-frame allocations:** The audio hot path allocates nothing (ArrayPool recycles buffers). `EnqueueWrite` ensures pool safety with `try/finally`.
- **ThreadLocal Random:** `FloatToPcm16` uses thread-local RNG for dither, no lock contention

### Timing & Sync

- **High-resolution timers:** `Stopwatch.GetTimestamp()` for precise timing calculations
- **Tick conversion:** `_tickMs = 1000.0 / Stopwatch.Frequency` constant for tick-to-ms conversion
- **Loopback silence detection:** `_lastLoopTick` + `LoopSilentMsThreshold` (200ms) detects when loopback is idle

---

## Things to Watch Out For (For LLMs/Developers)

### Critical Paths

1. **Audio Callbacks (`OnLoopbackData`, `OnMicData`)**
   - **DO NOT** allocate, block, or throw extensively
   - All disk I/O via `EnqueueWrite()` (which can block only if queue is full)
   - Metering accumulates locally, throttled events sent at ~50ms intervals

2. **Mic Ring Buffer Lock Contention**
   - Brief critical sections via `lock (_micRingLock)`
   - Keep lock duration minimal (just ring read/write operations)
   - Two writers: `OnMicData` adds, `OnLoopbackData` consumes

3. **Dispose Pattern Safety**
   - `RecorderEngine.Dispose()` must be idempotent
   - `_disposed` flag prevents use-after-dispose
   - All cleanup wrapped in try/catch for partial disposal safety

### Edge Cases

| Situation | Handling | Comment |
|-----------|----------|---------|
| Loopback silent >200ms | Mic drives output | Mix = mic-only, system = zeros |
| Mic underrun (FIFO empty) | Zero-fill + `_micUnderrunBlocks`/`_silencedSamples` | Pre-roll 2 blocks not counted; 1 Hz `lagTime` line |
| Mic overrun (FIFO over cap) | Drop-oldest + `_micOverrunBlocks`, throttled Warn | Bounded ~2 s cap; never grows unbounded |
| Queue full (disk stall) | Drops data to avoid callback stall | Logged immediately on first drop, then every 100 drops |
| Device disappears during record | Detach lost source, silence-bridge, backoff re-open same file | `HandleDeviceDisconnectedAsync` + `DetachSource`/`ReopenLostSourceLoopAsync`; `FinalizeWavFiles` keeps headers valid; plug-in resumes via `TryResumeLostSourcesAsync` |
| MP3 encode failure | Logs error, WAVs still saved | User gets WAV files even if MP3 fails |

### Configuration Gotchas

1. **Legacy Config Compatibility**
   - Oldconfigs used `Mp3Quality` string; new use `Mp3BitrateKbps` int
   - Migration logic in `AppSettings.Load()` parses string → int
   - `Mp3Quality` is nulled on save to prevent confusion

2. **Loopback Follows the Selected Speaker**
   - There is no separate loopback picker: loopback captures the selected speaker endpoint
   - The `LoopbackName` setting ("Auto (match speaker)") is legacy and currently unused

3. **Gain Application**
   - Gains applied **before** metering and **before** mixing
   - `MicGain` affects mic WAV, meter, and mix contribution
   - `LoopGain` affects system WAV, meter, and mix contribution
   - Mix formula: `sum = s*LoopGain + m*MicGain; scale = peak>1 ? 1/peak : 1; mix = limiter(sum*scale)` (no -6 dB penalty)

### Build/Distribution Notes

- **Self-contained builds:** Include .NET runtime (no user install needed)
- **Single-file publishing:** All resources extracted to temp at runtime
- **Architecture support:** Both `win-x64` and `win-arm64` targets for cross-architecture support
- **Version:** Single source of truth in `Directory.Build.props` (currently 0.3.3), bumped automatically by CI on push to master

### Known Limitations

1. **Windows-only** (WASAPI loopback is Windows-specific)
2. **Max of 1 simultaneous recording** (single RecorderEngine instance per MainWindow)
3. **No real-time effects** beyond gain/soft clip (plugins not supported)
4. **Fixed internal quanta** (scratch buffers sized from 1024-frame blocks, mix emits fixed 480-frame quanta) - not configurable
5. **Ring buffer capacity** is dynamic but may drop data if mic/loopback drifts severely

---

## Additional Documentation Files

- **WORKING_WITH_AUDIO.md** - Audio concepts, WASAPI details, DSP math
- **CONTRIBUTING.md** - Development setup, testing, pull request guidelines
- **TROUBLESHOOTING.md** - Common issues and solutions
