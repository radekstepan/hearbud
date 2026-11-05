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
- **.NET 9.0** targeting Windows
- **C# 13** with nullable reference types enabled
- **WPF (Windows Presentation Foundation)** for UI
- **Windows Forms** interop for folder browser dialog

### Key Dependencies
- **NAudio.Wasapi / NAudio.Core** (v3.3.0) - Audio capture and file/MP3 I/O
  - `WasapiRecorder` capture (loopback + mic, IAudioClient3, MMCSS)
  - `WaveFileReader` for the MP3 post-step
  - `MediaFoundationEncoder` for MP3 encoding
- **H.NotifyIcon.Wpf** (v2.3.0) - System tray icon support
- **System.Text.Json** (v9.0.0) - Settings serialization

### Audio APIs
- **WASAPI (Windows Audio Session API)** - Primary audio capture mechanism
- **Media Foundation** - MP3 encoding via NAudio wrapper

---

## High-Level Architecture

```
┌─────────────────────────────────────────────────────────────┐
│                      MainWindow (WPF)                       │
│  - Device selection, gain controls, output settings         │
│  - UI level meter updates via timer (100ms)                 │
└──────────────────────┬──────────────────────────────────────┘
                       │
                       │ Events: LevelChanged, Status, EncodingProgress
                       │
                       ▼
┌─────────────────────────────────────────────────────────────┐
│                   RecorderEngine (Core)                     │
│  - NAudio capture (loopback + mic, float32 @ 48 kHz)         │
│  - Single mixer thread: DSP + disk + meters                 │
│  - Timestamp sync (1 s horizon + PLL read rate)             │
└──────────────────────┬──────────────────────────────────────┘
                       │
                       │ Callbacks: copy → stamp QPC → channel
               ┌───────┴────────┐
               │                │
               ▼                ▼
┌──────────────────┐  ┌──────────────────┐
│ Loopback Capture │  │  Mic Capture     │
│ (WasapiRecorder) │  │ (WasapiRecorder, │
│                  │  │  raw mode)       │
└──────────────────┘  └──────────────────┘
```

### Threading Model

1. **UI Thread** - WPF main thread, handles user input and timer-based meter updates (100ms interval)
2. **Audio Threads** - NAudio MMCSS capture threads firing `DataAvailable` (~100 times/sec). Copy → stamp → `TryWrite`, nothing else.
3. **Mixer Thread** - Single 10 ms `PeriodicTimer` loop draining both channels: timestamp sync, DSP (gain → DC block → kneed limit), metering, hum tap, and RF64 disk writes
4. **MP3 Encoding** - Thread pool task during post-processing (masters already safe)

**Critical Design Rule:** Never block the audio callback threads. They hand blocks to bounded drop-oldest channels; the mixer owns everything downstream.

---

## Core Components

### 1. App / App.xaml.cs
**Purpose:** Application entry point and global exception handling

**Key Responsibilities:**
- Global crash handlers (AppDomain unhandled exceptions, dispatcher unhandled exceptions, task scheduler unobserved exceptions)
- Crash logging to `%LocalAppData%\Hearbud\logs.txt`

**Important Notes:**
- `CrashLog.LogAndShow()` is the single point of failure reporting
- All exceptions are caught to provide user-friendly crash messages before app termination

### 2. MainWindow / MainWindow.xaml + .cs
**Purpose:** Main UI window providing controls and feedback

**Key Responsibilities:**
- Device enumeration and selection (mic + speaker)
- Gain sliders (independent mic and system gains)
- Output settings (location, base filename, MP3 quality/bitrate)
- Real-time level meters (RMS + Peak with clip indication)
- Status messages and encoding progress
- Keyboard shortcuts: Ctrl+R (start), Ctrl+S (stop)
- System tray icon integration

**UI State Management:**
- `_uiReady` flag guards UI updates during initialization
- `_engine` (RecorderEngine) instance lives for window lifetime
- Device dictionaries (`_micDict`, `_spkDict`) hold name→id strings; the engine owns all WASAPI objects and surfaces `DevicesChanged` (see `AudioDeviceInfo` DTOs)

**Timer Behavior:**
- 100ms timer (`_uiTimer`) updates meters via `Dispatcher.Invoke()`
- Throttling prevents excessive UI redraws while still being responsive

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
| `_loop`, `_mic` | NAudio `WasapiRecorder` instances | Loopback + raw-mode mic, float32 @ 48 kHz |
| `_loopDev`, `_micDev` | Retained `MMDevice`s | Recorder holds them — must outlive recording |
| `_loopQ`, `_micQ` | Bounded drop-oldest channels | 256/512 blocks; full = drop + count, never block |
| `_loopFifo` | Loopback-side mixer FIFO | Mixer-owned; capped at 2 s with drop counting |
| `_micBuffer` | `TimestampedMicBuffer` sync | 1 s horizon + PLL read rate (see ADR-0010) |
| `_wavSys`, `_wavMic`, `_wavMix` | Owned RF64 writers | Mixer thread only; 2 s header re-patch |
| `_mixTask`, `_mixCts` | Mixer loop lifetime | 10 ms `PeriodicTimer`; cancel + 5 s join on stop |
| `_micGain`, `_loopGain` | Runtime-adjustable gains | `Volatile`, applied before mixing and metering |
| `_outRate`, `_outChannels` | Output format | Fixed 48 kHz stereo (capture AT target) |

#### Public API Methods

##### `MonitorAsync(RecorderStartOptions opts)`
- (Re)builds both capture legs at the target format, attaches callbacks, starts recording
- Starts device notifications and the mixer task (meters live, no file output)
- Always rebuilds (no stale-state optimization — correctness first)

##### `StartAsync(RecorderStartOptions opts)`
- Calls `MonitorAsync()` first (ensures devices open)
- Disk-space preflight: refuses to start below 20 MB free
- Generates unique output file paths (handles name collisions like `rec (1).wav`)
- Opens 3 RF64 tmp files + 1 log file (final names published atomically on stop)
- Resets sync buffer, counters, and the session QPC clock

##### `StopAsync()`
- Sets `_recording = false`, stops capture; the mixer keeps running to drain
- Bounded 30 s drain (channels + sync buffer + FIFO), then mixer shutdown
- Disposes writers (headers already fresh from the 2 s re-patch), publishes tmp → final
- Encodes MP3 (post-process) on a thread pool thread; masters already safe
- Closes log file, raises `Stopped` with the published paths

##### `Dispose()`
- Idempotent: cancels mixer, stops capture, disposes recorders/devices/writers
- Unregisters device notifications; leaves tmp files recoverable (orphan sweep)

### 4. AppSettings
**Purpose:** Persistent application settings

**Storage:** `%UserProfile%\.hearbud_config.json`

**Key Properties:**
- `MicName`, `SpeakerName` - Last selected devices
- `OutputDir` - Default save folder
- `Mp3BitrateKbps` - MP3 encoding bitrate (0 = Original/WAV only)
- `MicGain`, `LoopGain` - Persisted gain values
- `IncludeMic` - Whether to include mic in recording

**Legacy Handling:**
- Old configs used `Mp3Quality` string (e.g., "192 kbps")
- Migration logic parses numeric value from legacy string
- When loading new configs lacking `Mp3BitrateKbps`, regex extracts bitrate from `Mp3Quality`

### 5. Dbfs (Static Utility)
**Purpose:** Audio level calculations and formatting

**Key Methods:**
- `FormatGain(double value)` - Converts linear gain multiplier to string (e.g., "2.00× (+6.0 dB)")
- `ToDbfs(double peakLin)` - Converts linear peak to dBFS clamped to -60 dB floor

---

## Data Flow

### Audio Capture Flow

```
┌─────────────────────────────────────────────────────────────────────────┐
│                    NAudio MMCSS Capture Threads                         │
│              (one per leg: loopback + mic, ~100 Hz)                     │
└─────────────────────────┬───────────────────────────────────────────────┘
                          │
                          │ DataAvailable: float32 span + 100-ns QPC stamp
                          │ (copy → stamp → TryWrite; nothing else)
                          ▼
                 ┌───────────────────┐
                 │ Bounded channels  │  ← DropOldest: full = drop + count
                 │ _loopQ / _micQ    │     (256 / 512 blocks)
                 └─────────┬─────────┘
                           │
                           │ drained by the mixer, never awaited
                           ▼
                 ┌─────────────────────────────┐
                 │ Mixer tick (10 ms)          │
                 │  loopback → FIFO (paced)    │
                 │  mic → TimestampedMicBuffer │
                 │  (1 s horizon + PLL rate)   │
                 └──────────────┬──────────────┘
                                │
                 ┌──────────────▼──────────────┐
                 │ ProcessMixBlock: gains →    │
                 │ DC block → avg → kneed limit│
                 │ + per-leg RMS/peak meters   │
                 │ + hum tap on the mix        │
                 └──────────────┬──────────────┘
                                │
          ┌─────────────────────┼─────────────────────┐
          ▼                     ▼                     ▼
    ┌──────────┐          ┌──────────┐          ┌──────────┐
    │ Write Sys│          │ Write Mic│          │ Write Mix│
    │ RF64 WAV │          │ RF64 WAV │          │ RF64 WAV │
    └──────────┘          └──────────┘          └──────────┘
```

### Mixer & Disk I/O Flow

The mixer thread owns DSP **and** disk: each 10 ms tick drains the channels,
processes one block, and writes all three legs straight to the RF64 writers
(2 s header re-patch + `WriteThrough`, so a kill loses ≤2 s of tail).

**Key Points:**
- Channels are the only cross-thread handoff; everything downstream is one thread
- A disk stall slows the mixer, channels fill, oldest blocks drop **counted**
  (`_droppedBlocks` → `HealthReport`) — capture threads never stall
- Stop: capture halts, the mixer drains (bounded 30 s), then writers close and
  tmp files publish atomically

### MP3 Encoding Flow (Post-Process)

```
StopAsync() called
          │
          ▼
┌─────────────────────┐
│ Drain mixer         │  ← Bounded 30 s (channels + buffer + FIFO)
│ Close WAV files     │  ← Headers already fresh from the 2 s re-patch
│ Publish tmp → final │  ← Atomic rename, renumbered on collision
└─────────────────────┘
          │
          ▼
   if Mp3BitrateKbps > 0
          │
          ▼
┌─────────────────────┐   Task.Run on thread pool (masters already safe)
│ Task.Run {          │◄────────────────────────────────►
│   WaveFileReader   │   Read RF64 float mix
│   → 16-bit PCM     │   Via WaveToSampleProvider + SampleToWaveProvider16
│   MediaFoundation  │   EncodeToMp3 (synchronous; coarse 0/100 % progress)
│ }                  │
└─────────────────────┘
          │
          ▼
   RaiseStatus(Stopped)
```

---

## Design Decisions

### 1. Loopback-Paced Mixer
**Decision:** System/loopback audio paces the session; the mixer pulls everything available each tick, and mic is pulled to match by timestamp.

**Rationale:**
- Keeps system and mic perfectly synced in the mix
- Prevents "lining up at start" timing errors
- Mimics what happens in reality: mic hears what the speaker outputs

**Implications:**
- If mic is late, gaps are zero-filled (silence preserved, counted as underruns)
- If loopback goes idle, steady mic-only pulls keep the session rolling
- If both idle, the session pauses (no unbounded silence files)
- Stale mic is dropped by timestamp horizon — no "old mic over new system" echo

### 2. Three Parallel RF64 Float WAV Files
**Decision:** Always write separate `*-system.wav`, `*-mic.wav`, and `*-mix.wav` — all 48 kHz float32 stereo RF64.

**Rationale:**
- Users can later remix or edit individual tracks in DAW/Audacity
- One format everywhere: no conversion, no quantization, no 4 GB wall
- Avoids lossy capture step even if final output is MP3
- Headers re-patched every 2 s: a kill loses ≤2 s of tail, never the take

### 3. Single Mixer Thread + Drop-Oldest Channels
**Decision:** Capture hands blocks to bounded channels; one mixer thread owns DSP and disk.

**Rationale:**
- Audio callback threads must never block (WASAPI is unforgiving)
- One downstream thread means no lock pyramids and a single place to reason about backpressure
- Disk stalls slow the mixer; channels fill and drop oldest **counted** (`HealthReport`)

**Capacity Choice (256/512 blocks):**
- Seconds of absorb at typical packet rates; drops only under real stalls
- Every drop is visible in health instead of failing silently

### 4. Fresh Capture Arrays per Block (No Pooling at the Handoff)
**Decision:** Callbacks allocate one `float[]` per block instead of renting from a pool.

**Rationale:**
- ~200 small Gen0 arrays/sec is negligible for the GC
- Pooling would leak on every DropOldest eviction (evicted arrays are never returned)
- Simplicity + leak-proofing beats theoretical zero-alloc here

### 5. Timestamped Sync with PLL Read Rate
**Decision:** Mic blocks carry 100-ns QPC stamps; a 1 s horizon drops stale mic and a PLL servos depth to 120 ms by bending the read rate in [0.999, 1.001].

**Rationale:**
- Independent device crystals drift ~±50 ppm — chunk counting creeps apart
- Timestamps align by wall clock, not by counting; the servo absorbs drift continuously
- Fractional cursor applies the ratio with no per-block rounding jitter

**Sync Edge Case:** When loopback resumes after silence, mic older than 1 s is dropped by timestamp. This prevents "old mic over new system" echo effect.

### 6. Kneed Soft-Limiter (No Global tanh)
**Decision:** The mix passes through `SoftLimiter`: bit-transparent below the 0.8 knee, smoothly ceilinged to ±1.

**Rationale:**
- Global tanh dulled even quiet transients; the knee only touches hot signals
- Rounds off peaks instead of hard-clipping (no harsh digital distortion)

### 7. DC Blocking on Mix Legs Only
**Decision:** A ~8 Hz 1-pole high-pass runs on each mix leg before limiting; raw recordings stay bit-raw.

**Rationale:**
- Kills DC offset and subsonic rumble that eat headroom and confuse meters
- Voice and music pass essentially untouched

### 8. Float32 Masters for MP3 Quality
**Decision:** MP3 encodes from the RF64 float32 mix via float → samples → clipped 16-bit.

**Rationale:**
- MP3 encoder's own quantization (psychoacoustic) is the only loss
- No intermediate 16-bit file quantization step
- Masters are finalized and published before encoding starts, so encode failure only loses the `.mp3`

---

## Performance Optimizations

### Implemented Optimizations

| Optimization | Location | Impact |
|-------------|----------|--------|
| **Single mixer thread** | `MixerLoopAsync()` / `MixerTick()` | No locks on DSP/disk; one backpressure point |
| **Drop-oldest channels** | `CaptureHandoff` | Stalls drop counted, never block capture |
| **Preallocated mixer scratch** | `_sysBlock`/`_micBlock`/`_mixBlock`/`_ioBytes` | Power-of-2 growth, no per-tick allocs |
| **Throttled level updates** | `_lastLevelTick*` fields | ~50ms throttle reduces UI cross-thread calls |
| **Bounded FIFOs** | `_loopFifo` cap, 30 s mic cap | Unbounded growth impossible by construction |

### Memory Management

- **Pre-allocated buffers:** All significant float/byte arrays are pre-allocated and resized only when needed via `EnsureCapacity()` (power-of-2 sizing)
- **Small per-block allocs:** Capture allocates one `float[]` per block (~200/sec, negligible Gen0); the mixer itself allocates nothing per tick
- **No per-frame RNG/locks:** Float pipeline needs no dither and no conversion scratch

### Timing & Sync

- **High-resolution timers:** `Stopwatch.GetTimestamp()` for throttle/health timing
- **Tick conversion:** `_tickMs = 1000.0 / Stopwatch.Frequency` constant for tick-to-ms conversion
- **Audio timestamps:** NAudio 100-ns QPC stamps flow end to end into `TimestampedMicBuffer` (10 MHz domain, zero conversion)

---

## Things to Watch Out For (For LLMs/Developers)

### Critical Paths

1. **Audio Callbacks (`OnLoopData`, `OnMicData`)**
   - Copy → stamp → `TryWrite`, then return; never block, touch disk, or throw (a throw kills the capture thread)
   - ~200 small `float[]`/sec is fine; do NOT pool here (DropOldest would leak rents)
   - Metering happens on the mixer; throttled level events sent at ~50ms intervals

2. **Mixer Backpressure**
   - If the mixer stalls (slow disk), channels drop oldest **counted** — watch `HealthReport.DroppedBlocks`
   - `TimestampedMicBuffer` holds its own short lock (mic drain vs read are both mixer-side, so it never contends)

3. **Dispose Pattern Safety**
   - `RecorderEngine.Dispose()` must be idempotent
   - `_disposed` flag prevents use-after-dispose
   - All cleanup wrapped in try/catch for partial disposal safety

### Edge Cases

| Situation | Handling | Comment |
|-----------|----------|---------|
| Loopback idle, mic live | Steady 10 ms mic-only pulls | Mix = mic-only, system = zeros |
| Both idle | Session pauses | No unbounded silence files (same as v1) |
| Mic underrun (buffer short) | Zero-fill missing samples | Counted in `_micUnderrunBlocks` |
| Mic overrun (>30 s buffered) | Oldest frames dropped, counted | Only if loopback stalls while mic runs |
| Channel full (mixer stall) | Oldest block dropped, counted | Surfaced live in `HealthReport` |
| Device disappears during record | Per-leg auto-rebuild with backoff | Mixer coasts on zeros; session survives |
| MP3 encode failure | Logs error, WAVs still saved | User gets WAV files even if MP3 fails |

### Configuration Gotchas

1. **Legacy Config Compatibility**
   - Oldconfigs used `Mp3Quality` string; new use `Mp3BitrateKbps` int
   - Migration logic in `AppSettings.Load()` parses string → int
   - `Mp3Quality` is nulled on save to prevent confusion

2. **Auto-Match Loopback to Speaker**
   - Default: loopback device selected is same as selected speaker
   - User can manually choose different endpoint
   - "Auto (match speaker)" option in UI

3. **Gain Application**
   - Gains applied **before** metering and **before** mixing
   - `MicGain` affects mic WAV, meter, and mix contribution
   - `LoopGain` affects system WAV, meter, and mix contribution
   - Mix chain per sample: gain → DC block → average → kneed limit (see `MixerDsp`)

### Build/Distribution Notes

- **Self-contained builds:** Include .NET runtime (no user install needed)
- **Single-file publishing:** All resources extracted to temp at runtime
- **Architecture support:** Both `win-x64` and `win-arm64` targets for cross-architecture support
- **Version:** Single source of truth in `Directory.Build.props`, auto-bumped (patch) by CI on every push to master

### Known Limitations

1. **Windows-only** (WASAPI loopback is Windows-specific)
2. **Max of 1 simultaneous recording** (single RecorderEngine instance per MainWindow)
3. **No real-time effects** beyond gain/DC/limit (plugins not supported)
4. **Fixed 10 ms mixer tick** (loopback-driven pulls are variable-sized by design)
5. **MP3 encode is synchronous** (no mid-encode progress/cancel; masters are safe first)

---

## Additional Documentation Files

- **WORKING_WITH_AUDIO.md** - Audio concepts, WASAPI details, DSP math
- **CONTRIBUTING.md** - Development setup, testing, pull request guidelines
- **TROUBLESHOOTING.md** - Common issues and solutions
