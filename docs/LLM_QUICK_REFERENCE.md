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
├── App.xaml                # App entry point (global exception handlers)
├── AppSettings.cs          # Settings persistence (~/.hearbud_config.json)
├── Dbfs.cs                # Audio level (dBFS) calculations
├── MainWindow.xaml         # Main UI
├── MainWindow.cs           # UI logic, event handlers
└── RecorderEngine.cs       # CORE: Audio capture, mixing, async I/O
```

**Most Important File:** `RecorderEngine.cs` ~1065 lines - all audio pipeline logic

---

## Key Classes and Responsibilities

| Class | Lines | Purpose | Important Members |
|-------|-------|---------|------------------|
| `RecorderEngine` | ~1065 | Core audio engine | `Monitor()`, `Start()`, `StopAsync()`, `OnLoopbackData()`, `OnMicData()` |
| `MainWindow` | ~521 | UI, events, controls | `OnStart()`, `OnStop()`, `UpdateMeters()`, device selection |
| `AppSettings` | ~90 | Settings persistence | `Load()`, `Save()`, properties: `MicGain`, `LoopGain`, `Mp3BitrateKbps` |
| `Dbfs` | ~15 | Conversions | `FormatGain()`, `ToDbfs()` |
| `App` (xaml.cs) | ~30 | Global crash handling | `CrashLog.LogAndShow()` |

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

WASAPI Mic       → OnMicData() → ReadExactSamples() → ConvertToTarget()
                                              ↓ (resample/remix if needed)
                                      _micRing[] (ring buffer)
                                              ↓
                                      Mixed by OnLoopbackData
```

**Synchronization:** Loopback (system) is clock source. Mik pulled from ring buffer to match loopback chunks.

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

// Mix formula (preserve headroom)
float mix = (systemSample + micSample) * 0.5;
float mixClipped = SoftClipIfNeeded(mix);  // tanh + clamp
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
| `_micRing` | `float[]` | 4 sec @ 48kHz × 2ch ≈ 384 KB | Mic sync ring |
| `_loopBufF` | `float[]` | 8 × 1024 frames × 2ch ≈ 64 KB | Loopback temp |
| `_micConvBuf` | `float[]` | Same as above | Mic converted |
| `_tmpMicBlock` | `float[]` | Same | Pulled from ring |
| `_mixBufF` | `float[]` | Same | Mixed output |
| `_pcm16Sys` | `byte[]` | 2× above | System WAV bytes |
| `_pcm16Mix` | `byte[]` | 2× above | Mix WAV bytes |
| `_pcm32Mix` | `byte[]` | 4× above | 32-bit mix bytes |
| `_resampleScratch` | `float[]` | Same | Reusable for resampling |

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
| `MicGain` | double | 1.0 | Linear multiplier (0.0–3.0) |
| `LoopGain` | double | 1.0 | Linear multiplier (0.0–3.0) |
| `IncludeMic` | bool | true | Include mic in recording |

---

## Logging & Diagnostics

### Crash Log
- **Location:** `%LocalAppData%\Hearbud\logs.txt`
- **Captures:** Unhandled exceptions (AppDomain, Dispatcher, TaskScheduler)
- **Written by:** `CrashLog.LogAndShow(where, ex)`

### Session Log
- **Location:** `*.txt` next to output files (e.g., `rec-20250623_143022.txt`)
- **Per-Session:** Created in `Start()`, closed in `StopAsync()`
- **Events:** Start/stop, device info, per-block diagnostics

**Key Diagnostics:**
```text
[...] INFO OnLoopbackData: Mic ring backlog: 0.0523 s (max 0.1500 s)
```
- `backlog ≈ 0.1s` → Healthy sync
- `backlog ≈ 0.0s` → Underruns (mic too slow)
- `backlog ≈ 0.4s` → Overruns (mic too fast)

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

### Linear Resampling
```csharp
int srcIndex = (int)(dstFrame * (srcRate / dstRate));
float frac = (dstFrame * (srcRate / dstRate)) - srcIndex;
interpolated = src[srcIndex] * (1 - frac) + src[srcIndex + 1] * frac;
```

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
| `_monitoring = false, _recording = false` | Idle (stopped) | ctor/Dispose/StopMonitor | Monitor() |
| `_monitoring = true, _recording = false` | Monitoring (meters active) | Monitor() | StopMonitor() / Start() |
| `_monitoring = true, _recording = true` | Recording (meters + file I/O) | Start() | StopAsync() |

**Transitions:**
- `Idle → Monitor`: Open devices, subscribe callbacks
- `Monitor → Recording`: Open WAVs, init queue, reset counters
- `Recording → Monitor`: StopAsync complete, keep monitoring (unless full stop)
- *any* → `Idle`: StopMonitor, close devices

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
| Ctrl+S | Stop recording |

---

## Quick Navigation Index

When modifying specific functionality, look here:

| Task | File | Line Range (approx) | Key Methods |
|------|------|---------------------|-------------|
| Change mixing formula | RecorderEngine.cs | ~700-720 | `OnLoopbackData` mix loop |
| Add metering metrics | RecorderEngine.cs | ~560-620 | Metering accumulators in callbacks |
| Modify gain application | RecorderEngine.cs | ~580, 660 | Multipliers in metering and mix |
| Change dither algorithm | RecorderEngine.cs | ~870-890 | `FloatToPcm16()` |
| Adjust resampling | RecorderEngine.cs | ~830-860 | `ConvertToTarget()` |
| Add diagnostic logging | RecorderEngine.cs | ~970-990 | `Info()`, `Warn()`, `Error()` |
| Change UI layout | MainWindow.xaml | Entire file | XAML markup |
| Add UI events | MainWindow.xsl.cs | ~200-450 | Event handlers |
| Persist new setting | AppSettings.cs | Property + Load/Save | Add property, handle migration |

---

## Summary for LLMs

**Core Pillars:**
1. Non-blocking audio callbacks (use async I/O, pooling)
2. Loopback as clock source (mic via ring buffer)
3. Three parallel WAV outputs (system, mic, mix)
4. Throttled UI updates (100ms timer)
5. Comprehensive logging (crash + session)

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
