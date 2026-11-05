# LLM Quick Reference: Hearbud Codebase

This document provides a condensed overview of the Hearbud codebase, optimized for LLM consumption to quickly understand the project structure, key concepts, and areas to watch out for when making changes.

---

## Project Overview

**Hearbud** is a Windows desktop application that captures system audio (WASAPI loopback) and microphone simultaneously, writes RF64 float WAVs, and optionally encodes MP3. Built with .NET 9, WPF, and the NAudio audio library.

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
| `RecorderEngine` | ~1000 | Core audio engine | `MonitorAsync()`, `StartAsync()`, `StopAsync()`, `MixerTick()` |
| `MainWindow` | ~521 | UI, events, controls | `OnStart()`, `OnStop()`, `UpdateMeters()`, device selection |
| `AppSettings` | ~90 | Settings persistence | `Load()`, `Save()`, properties: `MicGain`, `LoopGain`, `Mp3BitrateKbps` |
| `Dbfs` | ~15 | Conversions | `FormatGain()`, `ToDbfs()` |
| `App` (xaml.cs) | ~30 | Global crash handling | `CrashLog.LogAndShow()` |

---

## Threading Model (CRITICAL for Audio)

| Thread | Purpose | Code Location |
|--------|---------|--------------|
| UI Thread | WPF, user input, timer (100ms) | `MainWindow` |
| Audio Loopback Thread | NAudio callback `OnLoopData` | MMCSS capture thread, ~100 Hz |
| Audio Mic Thread | NAudio callback `OnMicData` | MMCSS capture thread, ~100 Hz |
| Mixer Thread | DSP + disk + meters | `MixerTick()` on a 10 ms `PeriodicTimer` |
| MP3 Encoding Thread | Post-process encode | `Task.Run` in `StopAsync()` |

**ALWAYS Keep Audio Callbacks Non-Blocking:**
- ❌ NO I/O, locks, or throws (a throw kills the capture thread)
- ✅ OK: one `float[]` alloc, span copy, QPC stamp, channel `TryWrite`
- ✅ Everything else happens on the mixer thread

---

## Audio Signal Flow (Simplified)

```
WASAPI Loopback → OnLoopData() → float32 span → _loopQ (channel)
                                                      ↓
WASAPI Mic      → OnMicData()  → float32 span → _micQ (channel)
                                                      ↓
                                              MixerTick (10 ms):
                                              FIFO + TimestampedMicBuffer
                                                      ↓
                                              MixerDsp + meters + hum
                                                      ↓
                                              RF64 float32 disk write
```

**Synchronization:** Loopback paces the mixer; mic aligns by QPC timestamp (120 ms target, 1 s horizon, PLL read rate).

---

## Critical Design Contracts

### 1. Capture Handoff Pattern
```csharp
// Producer (audio callback): copy → stamp → hand off
var f = new float[frames * TargetCh];
span.Slice(0, f.Length * 4).CopyTo(MemoryMarshal.AsBytes(f.AsSpan()));
CaptureHandoff.TryWrite(q, new TimedBlock(f, qpc, frames), cap, ref _droppedBlocks);
// Consumer: the mixer thread drains both channels every 10 ms tick
```

### 2. Clock Source Contract
- Loopback paces the mixer: each tick pulls everything available
- Mic aligns by QPC timestamp to (loopNow − 120 ms); shortfalls zero-fill (counted)
- Mic older than 1 s is dropped by timestamp horizon (no stale echo)
- Loopback idle + mic live: steady 10 ms mic-only pulls; both idle: session pauses

### 3. Gain Application Order
```csharp
// Applied BEFORE metering AND mixing (see MixerDsp.ProcessMixBlock)
float s = sys[i] * sysGain;   // metered post-gain
float m = mic[i] * micGain;   // metered post-gain
s = dcSys.Filter(s, ch);      // mix legs only; raws stay raw
m = dcMic.Filter(m, ch);
float mix = SoftLimiter.Limit((s + m) * 0.5f);  // knee at 0.8
```

### 4. Dispose Idempotence Pattern
```csharp
private volatile bool _disposed;

public void Dispose() {
    if (_disposed) return;  // Early exit
    _disposed = true;

    try { _mixCts?.Cancel(); } catch { }  // Guard each
    StopInternal();  // detach, stop + dispose recorders/devices
    // ...
}
```

---

## Methods to Watch (Hot Path)

### Audio Callbacks (Don't Break These!)
- `OnLoopData(ReadOnlySpan<byte>, ...)` - Called ~100 Hz: copy → stamp → hand off
- `OnMicData(ReadOnlySpan<byte>, ...)` - Same; silence arrives as zero blocks

**Safe Operations:**
- Span copy via `MemoryMarshal.AsBytes()` - O(N), no locks
- `CaptureHandoff.TryWrite()` - drop-oldest channel, never blocks
- `MixerDsp.ProcessMixBlock()` - mixer-side DSP (gain → DC → limit + meters)

**Unsafe Operations (Don't Do):**
- ❌ Pooling at the handoff (DropOldest would leak rents — one `float[]` per block is fine)
- ❌ File I/O (the mixer thread owns all disk writes)
- ❌ `Thread.Sleep()`, `await Task.Delay()`, blocking calls, throws
- ❌ Complex LINQ (allocates)

---

## Buffer Management

### Pre-allocated Buffers (instance fields)
| Buffer | Type | Size | Purpose |
|--------|------|------|---------|
| `_micBuffer` | `TimestampedMicBuffer` | 4 s growable, 30 s cap | Timestamped mic sync |
| `_loopFifo` | `List<float>` | 2 s cap, drop-counted | Loopback-side mixer FIFO |
| `_sysBlock` / `_micBlock` | `float[]` | Block-sized, power-of-2 | Mixer leg scratch |
| `_mixBlock` | `float[]` | Block-sized, power-of-2 | Mixed output scratch |
| `_ioBytes` | `byte[]` | 4× float scratch | Float→byte staging for disk |

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
[...] INFO AfterBlock: Mic backlog: 0.0523 s (max 0.1500 s)
```
- `backlog ≈ 0.1s` → Healthy sync
- `backlog ≈ 0.0s` → Underruns (mic too slow)
- `backlog ≈ 0.4s` → Overruns (mic too fast)

---

## Common Gotchas (For LLMs/Developers)

### 1. Audio Callback Blocking
**Symptom:** Dropouts, glitches, UI freeze
**Check:** Callbacks do copy → stamp → handoff only (no I/O, locks, throws)
**Fix:** Move everything downstream; the mixer owns DSP + disk

### 2. Mixer Stall
**Symptom:** Audio gaps, rising `HealthReport.DroppedBlocks`
**Check:** Slow disk or oversized catch-up blocks after a stall
**Fix:** Keep `MixerTick()` fast; drops are counted, never silent

### 3. Dispose Race Condition
**Symptom:** `ObjectDisposedException` after StopAsync
**Check:** Callbacks still firing during dispose
**Fix:** Detach callbacks before disposing (`StopInternal` sets `_stopping` first)

### 4. Early MMDevice Disposal
**Symptom:** Crash in capture teardown/rebuild
**Check:** `MMDevice` disposed while its recorder is alive (the recorder retains it)
**Fix:** Dispose devices only on teardown/rebuild, after the recorder

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

### PLL Drift Bending (TimestampedMicBuffer)
```csharp
filtered += (depthMs - 120 - filtered) * 0.002;
ratio = Math.Clamp(1 + filtered * 0.00002, 0.999, 1.001);
pos = cursor + frame * ratio;  // fractional cursor persists across blocks
interpolated = src[i0] * (1 - t) + src[i0 + 1] * t;
```

### RMS Calculation
```csharp
double sumSq = 0;
for (int i = 0; i < count; i++) sumSq += samples[i] * samples[i];
rms = Math.Sqrt(sumSq / count);
```

### Kneed Limit (SoftLimiter)
```csharp
if (MathF.Abs(x) < 0.8f) return x;  // transparent below the knee
float over = MathF.Abs(x) - 0.8f;
return MathF.CopySign(MathF.Min(0.8f + over / (1 + over * 2.2f), 1f), x);
```

### DC Block (1-pole HPF)
```csharp
y = x - prevIn + 0.999f * prevOut;  // ~8 Hz @ 48 kHz, per channel
```

### Timestamp Horizon (mic sync)
```csharp
// On every read: drop mic older than loopNow - 1000 ms (by QPC stamp)
while (segment.EndQpc <= loopQpcNow - horizonQpc) DropOldest(segment);
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
| NAudio.Wasapi | 3.3.0 | Capture, devices, MP3 encode |
| NAudio.Core | 3.3.0 | WaveFormat, WaveFileReader, DSP |
| H.NotifyIcon.Wpf | 2.3.0 | System tray icon |
| System.Text.Json | 9.0.0 | Settings JSON |

---

## Build Configuration

**Framework:** `net9.0-windows`
**Runtime Identifiers:** `win-x64`, `win-arm64`
**Publish Mode:** Self-contained, single file, compressed
**Assembly Name:** Hearbud

**Targeted Versions:**
- Requires Windows 10/11 (WASAPI loopback)
- .NET 9 Desktop Runtime (or self-contained)

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
| Change mixing formula | MixerDsp.cs | `ProcessMixBlock` | Gain → DC → avg → limit |
| Add metering metrics | MixerDsp.cs / RecorderEngine.cs | `ProcessMixBlock` + throttle | Per-leg RMS/peak/clip |
| Modify gain application | MixerDsp.cs | `ProcessMixBlock` head | `Volatile` gain properties |
| Adjust drift servo | TimestampedMicBuffer.cs | `Read` (PLL block) | Target/horizon/ratio consts |
| Adjust hum detection | Goertzel.cs | `HumWatch` | Bins + threshold |
| Add diagnostic logging | RecorderEngine.cs | `Info()`, `Warn()`, `Error()` | 10 MB cap, `AfterBlock` cadence |
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
