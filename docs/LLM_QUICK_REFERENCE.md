# LLM Quick Reference: Hearbud Codebase

This document provides a condensed overview of the Hearbud codebase, optimized for LLM consumption to quickly understand the project structure, key concepts, and areas to watch out for when making changes.

---

## Project Overview

**Hearbud** is a Windows desktop application that captures system audio (WASAPI loopback) and microphone simultaneously, writes WAVs, and optionally encodes MP3. Built with .NET 8, WPF, and CSCore audio library.

**Key Output Files:**
- `*-system.wav` - 16-bit PCM, raw system audio
- `*-mic.wav` - 16-bit PCM, raw microphone
- `*-mix.wav` - 32-bit PCM, mixed audio with gains and soft clip
- `*.mp3` - Encoded mix (optional)

---

## File Structure

```
Hearbud/
├── App.xaml / .xaml.cs      # App entry point, crash dialog + debugger sink
├── AppSettings.cs           # Settings persistence (~/.hearbud_config.json)
├── Dbfs.cs                  # Audio level (dBFS) calculations
├── MainWindow.xaml          # Main UI (devices, audio, footer transport)
├── MainWindow.xaml.cs       # UI logic, event handlers
├── SettingsWindow.xaml      # Settings dialog (format/limits/countdown/hotkeys)
├── SettingsWindow.xaml.cs
├── UsbTopology.cs           # Mic+mouse shared-USB-hub probe
├── UiDiagnostics.cs         # Env report, input ring, lag tracker
├── HistoryStore.cs          # Session history JSON (currently unwired from UI)
├── SessionManifest.cs       # Crash bookkeeping (Begin/MarkClosed)
├── WavRepair.cs             # WAV header fix + unclosed-session recovery
├── WriterHealth.cs          # Disk-full / high-water tracking
└── RecorderEngine.cs        # CORE: Audio capture, mixing, async I/O
```

**Most Important File:** `RecorderEngine.cs` ~2600 lines - all audio pipeline logic

---

## Key Classes and Responsibilities

| Class | Lines | Purpose | Important Members |
|-------|-------|---------|------------------|
| `RecorderEngine` | ~2600 | Core audio engine | `MonitorAsync()`, `MonitorSourcesAsync()`, `StartAsync()`, `StopAsync()`, `OnLoopbackData()`, `OnMicData()` |
| `MainWindow` | ~1550 | UI, events, controls | `OnStart()`, `OnStop()`, `UpdateMeters()`, device selection |
| `SettingsWindow` | ~265 | Settings dialog | `OnOk()`, `OnCancel()`, hotkey capture |
| `AppSettings` | ~160 | Settings persistence | `Load()`, `Save()`, gains, includes, limits, hotkeys |
| `Dbfs` | ~26 | Conversions | `FormatGain()`, `ToDbfs()` |
| `App` (xaml.cs) | ~71 | Global crash handling | `CrashLog.LogAndShow()` |

---

## Threading Model (CRITICAL for Audio)

| Thread | Purpose | Code Location |
|--------|---------|--------------|
| UI Thread | WPF, user input, timer (100ms) | `MainWindow` |
| Audio Loopback Thread | WASAPI callback `OnLoopbackData` | CSCore-managed, ~100 Hz |
| Audio Mic Thread | WASAPI callback `OnMicData` | CSCore-managed, ~100 Hz |
| Disk Writer Thread | Consumer of `_writeQueue` | `DiskWriteLoop()` (Task) |
| MP3 Encoding Thread | Post-process encode | `Task.Run` in `StopAsync()` |

**ALWAYS Keep Audio Callbacks Non-Blocking:**
- ❌ NO allocations, I/O, locks (except quick ones), throws
- ✅ OK: Math, array indexing, atomic operations
- ✅ Offload I/O via `_writeQueue` (BlockingCollection)

---

## Audio Signal Flow (Simplified)

```
WASAPI Loopback → OnLoopbackData() → ReadExactSamples() → Convert to float
                          ↓
                  Metering (RMS/Peak) accumulators
                          ↓
               EnqueueWrite() → _writeQueue (bg thread writes WAV)

WASAPI Mic       → OnMicData() → ReadExactSamples() → ConvertToTargetChunked()
                                              ↓ (windowed-sinc resample/remix)
                                      _micRing[] (FIFO, dual-FIFO with loop)
                                              ↓
                                      Fixed 480-frame quanta mixed on loop ticks
```

**Synchronization:** Loopback (system) is clock source. Mic pulled from FIFO to match loopback chunks.

---

## Critical Design Contracts

### 1. Async I/O Pattern
```csharp
// Producer (audio callback)
byte[] rented = ArrayPool<byte>.Shared.Rent(count);  // Rent
Array.Copy(source, 0, rented, 0, count);         // Copy
_writeQueue.Add(new AudioWriteJob(target, rented, count));  // Queue

// Consumer (DiskWriteLoop)
foreach (var job in _writeQueue.GetConsumingEnumerable()) {
    wavWriter?.Write(job.Data, 0, job.Count);
    ArrayPool<byte>.Shared.Return(job.Data);  // Return to pool
}
```

### 2. Clock Source Contract
- Loopback drives timing: when loopback fires, pull same-sized mic chunk from ring
- If mic ring empty: zero-fill gap (silence preserved)
- If mic ring overflow: drop oldest samples (newest kept)
- When loopback silent >200ms: mic drives output alone
- When loopback resumes: ring buffer CLEARED (old mic dropped)

### 3. Gain Application Order
```csharp
// Applied BEFORE metering AND mixing
float systemSample = loopBufF[i] * LoopGain;
float micSample = micBufF[i] * MicGain;

// Mix formula: fixed -6 dB headroom, limit only on overs (transparent otherwise)
float mix = (systemSample + micSample) * 0.5f;
mix = ApplyMixLimit(mix, blockPeak);   // 1/peak memoryless per-frame cap, no release envelope
float mixClipped = SoftClipIfNeeded(mix);  // tanh only above ±1, then clamp
```

### 4. Dispose Idempotence Pattern
```csharp
private volatile bool _disposed;

public void Dispose() {
    if (_disposed) return;  // Early exit
    _disposed = true;

    try { _loopCap?.Dispose(); } catch { }  // Guard each
    try { _micCap?.Dispose(); } catch { }
    // ...
}
```

---

## Methods to Watch (Hot Path)

### Audio Callbacks (Don't Break These!)
- `OnLoopbackData(object, DataAvailableEventArgs)` - Called ~100 Hz, MUST NOT block
- `OnMicData(object, DataAvailableEventArgs)` - Same, watch for underruns

**Safe Operations:**
- `ReadExactSamples()` - CSCore read, O(N)
- `EnsureCapacity()` - Array resize (rarely)
- `ConvertToTarget()` - Resample/remix, minimal allocations
- `FloatToPcm16/Pcm32()` - Convert output buffers

**Unsafe Operations (Don't Do):**
- ❌ `new ...[]` allocations (use pooling)
- ❌ File I/O (use `_writeQueue`)
- ❌ `Thread.Sleep()`, `await Task.Delay()`, blocking calls
- ❌ Complex LINQ (allocates)

---

## Buffer Management

### Pre-allocated Buffers (instance fields)
| Buffer | Type | Size | Purpose |
|--------|------|------|---------|
| `_micRing` | `float[]` | 192k floats ≈ 2 s stereo @ 48 kHz | Mic sync FIFO |
| `_loopRing` | `float[]` | Same as above | Loopback FIFO (dual-FIFO) |
| `_loopBufF` | `float[]` | `BlockFrames`×8 floats ≈ 32 KB | Loopback temp |
| `_micConvBuf` | `float[]` | Same as above | Mic converted |
| `_tmpMicBlock` | `float[]` | Same | Pulled from ring |
| `_mixBufF` | `float[]` | Same | Mixed output (480-frame quanta) |
| `_pcm16Sys` / `_pcm16Mic` / `_pcm16Mix` | `byte[]` | `BlockFrames`×8×2 ≈ 16 KB each | 16-bit WAV bytes |
| `_pcm32Mix` | `byte[]` | Sized per block | 32-bit mix bytes |
| `_resampleScratch` | `float[]` | `BlockFrames`×8 floats | Reusable for resampling |

**Resizing:** `EnsureCapacity()` resizes to next power-of-2, but rarely needed if initial sizes sufficient.

### Pooled Buffers (Disk I/O)
- Rented per block: `ArrayPool<byte>.Shared.Rent(count)`
- Returned after write: `ArrayPool<byte>.Shared.Return(data)`
- Job struct: `AudioWriteJob { Target, Data, Count }`

---

## Configuration & Settings

**File:** `~/.hearbud_config.json` (per user)

**Legacy Compatibility:**
- Old config: `Mp3Quality = "192 kbps"` (string)
- New config: `Mp3BitrateKbps = 192` (int)
- Migration: Regex parse on load, null `Mp3Quality` on save

**Key Settings:**
| Property | Type | Default | Notes |
|----------|------|---------|-------|
| `MicName` | string | "" | Last selected mic device |
| `SpeakerName` | string | "" | Last selected speaker |
| `OutputDir` | string | `~/Music/Recordings` | Save location |
| `Mp3BitrateKbps` | int | 192 | 0 = Original (WAV only) |
| `MicGain` | double | 1.0 | Linear multiplier (slider 0.0–3.0) |
| `LoopGain` | double | 1.0 | Linear multiplier (slider 0.0–3.0) |
| `IncludeMic` / `IncludeSystem` | bool | true | Include source (idle soft-mute; excluded from record set) |
| `WriteStems` | bool | true | false = mix only |
| `AutoStopMinutes` / `AutoStopSizeMb` | double | 0 | 0 = off |
| `SplitInsteadOfStop` | bool | false | Split into `(N)` file instead of stopping |
| `CountdownSeconds` | int | 0 | 0/3/5/10, pre-start delay |
| `Start/Pause/StopHotkey` | string | Ctrl+R/P/S | Remappable, converter-validated |

---

## Logging & Diagnostics

### Crash Diagnostics
- **No log file:** `CrashLog.LogAndShow(where, ex)` shows the exception in the
  crash dialog and writes the same text to the debugger only.
- **Captures:** Unhandled exceptions (AppDomain, Dispatcher, TaskScheduler)

### Session Log
- **Location:** `*.txt` next to output files (e.g., `rec-20250623_143022.txt`)
- **Per-Session:** Created in `StartAsync()`, closed in `StopAsync()`
- **Events:** Start/stop, device info, per-block diagnostics

**Key Diagnostics (1 Hz `lagTime` line):**
```text
[...] INFO Info: lagTime=0.052s micDepth=… over=0 loopOver=0 under=0 silenced=0 backlogMax=0.120s masterFrames=… resyncs=0 lagWatch=ok
```
- `lagTime` small + `lagWatch=ok` → Healthy sync
- `under`/`silenced` climbing → Underruns (mic too slow)
- `over` climbing → Overruns (mic too fast)
- `lagWatch=LAG` → Backlog over 80 ms (drift/resync territory, see `resyncs=`)

---

## Common Gotchas (For LLMs/Developers)

### 1. Audio Callback Blocking
**Symptom:** Dropouts, glitches, UI freeze
**Check:** No `new`, `File.Write()`, `Thread.Sleep()` in callbacks
**Fix:** Use `_writeQueue`, `ArrayPool`, pre-allocated buffers

### 2. Ring Buffer Lock Contention
**Symptom:** Audio gaps, underruns
**Check:** `lock(_micRingLock)` held too long
**Fix:** Keep lock minimal, avoid I/O/allocations inside

### 3. Dispose Race Condition
**Symptom:** `ObjectDisposedException` after StopAsync
**Check:** Callbacks still firing during dispose
**Fix:** Stop callbacks before disposing (`TryStopDispose`)

### 4. Memory Leak (Forgotten Returns)
**Symptom:** Memory grows, GC pressure
**Check:** Missing `ArrayPool.Return()` in `DiskWriteLoop`
**Fix:** `finally { ArrayPool<byte>.Shared.Return(job.Data); }`

### 5. Clock Drift (Mic/Loopback Mismatch)
**Symptom:** Delayed mic, sync issues
**Check:** Session log backlog values
**Fix:** Normally handled; if severe, consider device change/driver

### 6. MP3 Encode Failure (Mix WAV Empty)
**Symptom:** Only system/mic WAVs, no MP3
**Check:** `*_mix.wav` exists and non-empty
**Fix:** Disk write failed; check permissions/space

---

## Key Algorithms (Reference)

### Windowed-Sinc Resampling (`ConvertToTargetChunked`)
Mic → target rate via a 12-tap polyphase windowed-sinc kernel (`SincPolyphase` table) with fractional phase carried across callbacks in `ResamplerState`, so chunk boundaries never click. Cumulative length stays within ±1 frame/s. (The old linear-interpolation resampler is gone.)

### RMS Calculation
```csharp
double sumSq = 0;
for (int i = 0; i < count; i++) sumSq += samples[i] * samples[i];
rms = Math.Sqrt(sumSq / count);
```

### Soft Clip (tanh)
```csharp
float SoftClip(float x) {
    if (x > 1.0 || x < -1.0) x = MathF.Tanh(x);
    return Math.Clamp(x, -1.0f, 1.0f);
}
```

### TPDF Dither
```csharp
float dither = (float)rng.NextDouble() - (float)rng.NextDouble();  // -1.0 to +1.0
float dithered = value * 32767.0f + dither;
short sample = (short)Math.Clamp(MathF.Round(dithered), short.MinValue, short.MaxValue);
```

### Ring Buffer Index Math
```csharp
// Write
_micRing[_micW] = value;
_micW = (_micW + 1) % _micRing.Length;
if (_micCount < _micRing.Length) _micCount++;
else _micR = (_micR + 1) % _micRing.Length;  // Overwrite oldest

// Read (pull multiple)
int read = Math.Min(count, _micCount);
for (int i = 0; i < read; i++) {
    dest[i] = _micRing[_micR];
    _micR = (_micR + 1) % _micRing.Length;
}
_micCount -= read;
```

---

## State Machine (RecorderEngine)

| State | Meaning | Entry | Exit |
|-------|---------|-------|------|
| `_monitoring = false, _recording = false` | Idle (stopped) | ctor/Dispose/StopMonitor | MonitorAsync() |
| `_monitoring = true, _recording = false` | Monitoring (meters active) | MonitorAsync() | StopMonitor() / StartAsync() |
| `_monitoring = true, _recording = true` | Recording (meters + file I/O) | StartAsync() | StopAsync() |

**Transitions:**
- `Idle → Monitor`: Open devices, subscribe callbacks
- `Monitor → Recording`: Open WAVs, init queue, reset counters
- `Recording → Monitor`: StopAsync complete, keep monitoring (unless full stop)
- *any* → `Idle`: StopMonitor, close devices
- Include toggles while idle go through `MonitorSourcesAsync()` (soft-mute, no teardown)

---

## Dependencies (NuGet)

| Package | Version | Usage |
|---------|---------|-------|
| CSCore | 1.2.1.2 | Audio capture, APIs, WAV, MP3 |
| H.NotifyIcon.Wpf | 2.3.0 | System tray icon |
| System.Text.Json | 8.0.5 | Settings JSON |

---

## Build Configuration

**Framework:** `net8.0-windows`
**Runtime Identifiers:** `win-x64`, `win-arm64`
**Publish Mode:** Self-contained, single file, compressed
**Assembly Name:** Hearbud

**Targeted Versions:**
- Requires Windows 10/11 (WASAPI loopback)
- .NET 8 Desktop Runtime (or self-contained)

---

## Keyboard Shortcuts

| Shortcut | Action |
|----------|--------|
| Ctrl+R | Start recording |
| Ctrl+P | Pause / resume |
| Ctrl+S | Stop recording |

(Defaults; remappable in the Settings dialog.)

---

## Quick Navigation Index

When modifying specific functionality, look here:

| Task | File | Key Methods |
|------|------|-------------|
| Change mixing formula | RecorderEngine.cs | `EmitFixedMix()` / `EmitMicOnlyFixed()`, `ComputeMixScale()` |
| Add metering metrics | RecorderEngine.cs | Metering accumulators in `OnLoopbackData()` / `OnMicData()` |
| Modify gain application | RecorderEngine.cs | `MicGain` / `LoopGain` multipliers in metering and mix |
| Change dither algorithm | RecorderEngine.cs | `FloatToPcm16()` |
| Adjust resampling | RecorderEngine.cs | `ConvertToTargetChunked()`, `SincPolyphase` |
| Add diagnostic logging | RecorderEngine.cs | `Info()`, `Warn()`, `Error()` |
| Change main UI layout | MainWindow.xaml | XAML markup |
| Change settings UI | SettingsWindow.xaml | XAML markup |
| Add UI events | MainWindow.xaml.cs | `OnStart()`, `OnStop()`, … event handlers |
| Persist new setting | AppSettings.cs | Property + `Load()`/`Save()`, handle migration |

---

## Summary for LLMs

**Core Pillars:**
1. Non-blocking audio callbacks (use async I/O, pooling)
2. Loopback as clock source (mic via FIFO, fixed 480-frame quanta)
3. Parallel WAV outputs (mix always, stems optional)
4. Throttled UI updates (100ms `DispatcherTimer`, engine level events at 50ms)
5. Session logging (`*.txt`); crashes go to dialog + debugger only (no log file)

**What to Watch:**
- Audio callback execution time (keep <1 ms)
- Memory allocation in hot paths (use pool)
- Thread safety (ring buffer lock, dispose flag)
- Resource cleanup (IDisposable on all audio objects)

**Good for LLMs:**
- Well-structured, clear separation of concerns
- Extensive inline comments and diagnostics
- Defensive coding (try/catch everywhere)
- Performance-focused (pooling, zero-alloc callbacks)

---

For more detail, see full documentation:
- [ARCHITECTURE.md](ARCHITECTURE.md) - Complete system documentation
- [WORKING_WITH_AUDIO.md](WORKING_WITH_AUDIO.md) - Audio/DSP theory
- [TROUBLESHOOTING.md](TROUBLESHOOTING.md) - Issue patterns and solutions
