# Hearing You: LLM Development Guide

Hello! I'm GitHub Copilot using glm-4.7, and I've prepared this guide to help you quickly understand the Hearbud codebase and work effectively with it.

## 🚀 Quick Start

**What is Hearbud?**
A Windows app that records system audio (WASAPI loopback) and microphone simultaneously, writing WAV files and optionally MP3.

**Tech Stack:**
- .NET 9.0 (C#) with WPF for UI
- NAudio 3.3 for capture (`WasapiRecorder`), WAV/MP3 I/O
- Windows WASAPI for loopback capture (shared mode, float32 @ 48 kHz)

---

## 📁 Essential Files (Read These)

| File | Priority | Why It Matters |
|------|-----------|----------------|
| `Hearbud/RecorderEngine.cs` | 🔴 CRITICAL | Capture + mixer + output (~1000 lines) |
| `Hearbud/TimestampedMicBuffer.cs` | 🔴 CRITICAL | Timestamp sync (horizon + PLL) |
| `Hearbud/MixerDsp.cs`, `SoftLimiter.cs`, `DcBlocker.cs` | 🟡 Important | Mix DSP chain |
| `Hearbud/MainWindow.xaml.cs` | 🟡 Important | UI logic, event handlers |
| `docs/ARCHITECTURE.md` | 🔴 CRITICAL | System architecture and design decisions |
| `docs/adr/` | 🟡 Important | Decision records 0001–0012 (0001/0002 fully, 0003/0008 partially superseded by 0012) |

---

## 🎯 Key Concepts

### 1. Threading Model (Don't Break This!)
```
UI Thread (WPF)              → User input, meters (100ms)
Audio Callback Threads (×2)  → copy → stamp QPC → channel TryWrite (~100 Hz)
        ↑                         ↑
        │                         │
        └─ MUST NOT BLOCK ────────┘
Mixer Thread                 → 10 ms tick: sync + DSP + meters + RF64 disk
```

**Golden Rule:** Audio callbacks (`OnLoopData`, `OnMicData`) copy, stamp, hand off — then return. No I/O, no locks, no throws (a throw kills the capture thread).

### 2. Audio Flow
```
System Audio (Loopback)              Microphone
      ↓                                    ↓
OnLoopData()                      OnMicData()
      ↓                                    ↓
_loopQ (channel)              _micQ (channel)
      ↓                                    ↓
  loopback FIFO              TimestampedMicBuffer (horizon + PLL)
      ↓                                    ↓
              MixerDsp: gains → DC → avg → kneed limit
                            ↓
                   RF64 float32 disk write
```

**Clock Sync:** Loopback paces the mixer; mic aligns by QPC timestamp (120 ms target, 1 s horizon, PLL read rate).

### 3. Output Files
- `*-system.wav` - float32 RF64, raw system
- `*-mic.wav` - float32 RF64, raw mic (captured at target — no conversion)
- `*-mix.wav` - float32 RF64, mixed with gains, DC block, kneed limit
- `*.mp3` - Encoded mix (optional, post-process after masters are safe)

---

## ⚠️ Common Pitfalls (Watch Out!)

| Mistake | Symptom | Fix |
|---------|---------|-----|
| Pooling at the handoff | Leaked buffers on DropOldest eviction | One `float[]` per block is fine (~200/sec) |
| File I/O in callback | Dropouts, glitches | Mixer thread owns all disk writes |
| Throwing in callback | Capture thread dies | Catch + `Error()` log, never rethrow |
| Disposing the `MMDevice` early | Crash (recorder retains it) | Dispose devices only on teardown/rebuild |
| Modifying gain during recording | Already safe | Gain properties are runtime-adjustable (`Volatile`) |
| Blocking the mixer tick | Channel drops | Keep `MixerTick()` fast; drops surface in `HealthReport` |

---

## 🔧 When Making Changes

### Adding New Settings
1. Add property to `AppSettings.cs`
2. Bind in `MainWindow.xaml` (UI)
3. Load/save in `LoadSettingsToUi()` / `SaveSettings()`
4. Handle legacy migration if needed

### Changing Audio Pipeline
1. Read `docs/WORKING_WITH_AUDIO.md` for DSP theory
2. Modify `RecorderEngine.cs` callbacks or DSP methods
3. Test with different devices/mic configurations
4. Check session logs for diagnostics

### Adding UI Features
1. Edit `MainWindow.xaml` (layout)
2. Add event handlers in `MainWindow.cs`
3. Update settings model if needed
4. Test on Windows (WPF-specific)

Performance Optimization
1. Profile with Visual Studio Diagnostic Tools
2. Focus on audio callbacks first (hot path)
3. Use `ArrayPool` for temporary buffers
4. Measure before/after (CPU time, allocations)

---

## 📊 State at a Glance

### RecorderEngine States
- `_monitoring = false` → Idle (devices closed)
- `_monitoring = true, _recording = false` → Monitoring (meters active)
- `_monitoring = true, _recording = true` → Recording (meters+file I/O)

### Buffer Allocation
- Mic sync buffer: 4 s growable, 30 s cap
- Loopback FIFO: 2 s cap with drop counting
- Mixer scratch: block-sized, power-of-2 growth

### Callback Frequency
- Audio callbacks: ~100 Hz per leg
- Mixer tick: 10 ms (`PeriodicTimer`)
- UI meter timer: 100 ms (10 Hz)
- Diagnostic logging: Every 50 mixer blocks

---

## 🔎 Quick Debugging

### Audio Callback Not Firing
- Check device is actually active (not muted/disabled)
- Check session log for rebuild lines (`RecoverAsync` retries automatically)
- Look at `Windows Audio Service` (services.msc)

### Memory Leak
- Profile Diagnostic Tools (VM, GC heap size)
- Watch channel counts / FIFO sizes under disk stalls
- Verify `Dispose()` called on all audio objects (`MMDevice`s retained!)

### Poor Audio Quality
- Kneed limiter active? (transparent below 0.8, ceilinged to ±1)
- DC blocked on mix legs? (`DcBlocker`, mix only)
- Mains hum? Check `HealthReport` / Troubleshooting Guide (hum triage)

### Sync Issues
- Check session log `Mic backlog: X s`
- ~0.12s = PLL locked, healthy
- ~0.0s = underruns (mic slow/missing)
- Growing = overruns (mic much faster than loopback)

---

## 📚 Deep Dive Resources

- **Full Architecture:** `docs/ARCHITECTURE.md`
- **Audio Math:** `docs/WORKING_WITH_AUDIO.md`
- **Dev Workflow:** `docs/CONTRIBUTING.md`
- **Troubleshooting:** `docs/TROUBLESHOOTING.md`
- **Quick Reference:** `docs/LLM_QUICK_REFERENCE.md`

---

## ✅ Health Check Before Pushing

1. ✅ Audio callbacks do copy → stamp → handoff only (no I/O, locks, throws)
2. ✅ No pooling at the drop-oldest handoff (would leak on eviction)
3. ✅ `IDisposable` pattern implemented on new audio resources
4. ✅ Session logs helpful (device info, diagnostics)
5. ✅ Settings migration handled (if changed)
6. ✅ No `Thread.Sleep()`, `await`, blocking calls in callbacks
7. ✅ Mixer tick stays fast (drops are counted, never silent)
8. ✅ Dispose idempotent (`if (_disposed) return;`)

---

## 💡 Tips from the Original Codebase

1. **Defensive Coding:** Almost every resource disposal wrapped in try/catch
2. **Logging Pervasive:** `Info()`, `Warn()`, `Error()` throughout for crash analysis
3. **Performance First:** One small alloc per block at capture; mixer allocates nothing per tick
4. **User Experience:** Throttled UI updates, clear error messages
5. **Single Owner:** The mixer thread owns DSP + disk — no lock pyramids

---

## 🎓 Learning Path for LLMs

1. **First:** Read `docs/ARCHITECTURE.md` - understand overall design
2. **Second:** Read `HLM_QUICK_REFERENCE.md` - get the code structure
3. **Third:** Skim `RecorderEngine.cs` - see actual implementation
4. **Fourth:** Deep dive `docs/WORKING_WITH_AUDIO.md` - audio theory
5. **Fifth:** Reference `docs/CONTRIBUTING.md` - coding conventions

---

## 🤝 I'm Here to Help!

Ask me about:
- Specific code sections (I can read any file)
- Audio concepts and DSP math
- Troubleshooting issues
- Architecture decisions
- Performance optimization ideas
- Code refactoring suggestions

Let's make Hearbud even better! 🎧✨
