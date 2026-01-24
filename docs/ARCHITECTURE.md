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

1. **UI Thread** - WPF main thread, handles user input and timer-based meter updates (100ms interval)
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
- Device dictionaries (`_micDict`, `_spkDict`) hold `MMDevice` objects for quick access

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
| `_loopCap`, `_micCap` | WASAPI capture instances | `WasapiLoopbackCapture` / `WasapiCapture` |
| `_loopIn`, `_micIn` | CSCore `SoundInSource` wrappers | FillWithZeros = true for safety |
| `_wavSys`, `_wavMic`, `_wavMix` | Wave file writers | Managed by separate thread |
| `_writeQueue`, `_writeTask` | Async I/O queue | BlockingCollection with capacity 2000 |
| `_micRing` | Mic audio ring buffer | Synchronized via `_micRingLock` |
| `_micGain`, `_loopGain` | Runtime-adjustable gains | Applied before mixing and metering |
| `_outRate`, `_outChannels` | Output format | Matches loopback device |

#### Public API Methods

##### `Monitor(RecorderStartOptions opts)`
- Opens audio devices (if needed)
- Starts audio streams (but no file output)
- Activates level metering
- Auto-restarts if monitoring already active

##### `Start(RecorderStartOptions opts)`
- Calls `Monitor()` first (ensures devices open)
- Generates unique output file paths (handles name collisions like `rec (1).wav`)
- Opens 3 parallel WAV files and 1 log file
- Initializes async write queue and background writer thread
- Resets mic ring buffer and counters

##### `StopAsync()`
- Sets `_recording = false`
- Drains write queue: calls `_writeQueue.CompleteAdding()` and awaits `_writeTask`
- **Critical:** WAV headers are finalized on `Dispose` after queue drains
- Encodes MP3 (post-process) on thread pool thread
- Closes log file

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
                 │ Mix: s + m * 0.5   │  ← Apply gains in both loops
                 │ + SoftClipIfNeeded │
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

### 2. Three Parallel WAV Files
**Decision:** Always write separate `*-system.wav`, `*-mic.wav`, and `*-mix.wav` files.

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

### 5. Ring Buffer for Mic Synchronization
**Decision:** Mic audio goes into a ring buffer pulled by loopback ticks.

**Rationale:**
- Handles mic/loopback sample rate differences (resampling in ConvertToTarget)
- Mic typically arrives at slightly different rate or chunk sizes
- Ring buffer natural overruns/underruns handled gracefully

**Sync Edge Case:** When loopback resumes after silence, existing mic backlog is dropped. This prevents "old mic over new system" echo effect.

### 6. Soft Clipping (tanh) vs Hard Clipping
**Decision:** Apply `Math.Tanh()` on samples exceeding ±1.0 before clamp.

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
| Mic underrun (ring empty) | Zero-fill missing samples | Counted in `_micUnderrunBlocks` |
| Mic overrun (ring full) | Oldest samples dropped | Happens when mic is much faster than loopback |
| Queue full (disk stall) | Drops data to avoid callback stall | Logged immediately on first drop, then every 100 drops |
| Device disappears during record | Exception in callback | Caught, logged, continues if possible |
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
   - Mix formula: `(s * LoopGain + m * MicGain) * 0.5`

### Build/Distribution Notes

- **Self-contained builds:** Include .NET runtime (no user install needed)
- **Single-file publishing:** All resources extracted to temp at runtime
- **Architecture support:** Both `win-x64` and `win-arm64` targets for cross-architecture support
- **Version:** Assembly version in `Hearbud.csproj` (currently 0.2.7)

### Known Limitations

1. **Windows-only** (WASAPI loopback is Windows-specific)
2. **Max of 1 simultaneous recording** (single RecorderEngine instance per MainWindow)
3. **No real-time effects** beyond gain/soft clip (plugins not supported)
4. **Fixed block size** (1024 frames) - not configurable
5. **Ring buffer capacity** is dynamic but may drop data if mic/loopback drifts severely

---

## Additional Documentation Files

- **WORKING_WITH_AUDIO.md** - Audio concepts, WASAPI details, DSP math
- **CONTRIBUTING.md** - Development setup, testing, pull request guidelines
- **TROUBLESHOOTING.md** - Common issues and solutions
