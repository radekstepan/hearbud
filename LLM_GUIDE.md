# Hearing You: LLM Development Guide

Hello! I'm GitHub Copilot using glm-4.7, and I've prepared this guide to help you quickly understand the Hearbud codebase and work effectively with it.

## 🚀 Quick Start

**What is Hearbud?**
A Windows app that records system audio (WASAPI loopback) and microphone simultaneously, writing WAV files and optionally MP3.

**Tech Stack:**
- .NET 8.0 (C#) with WPF for UI
- CSCore library for audio capture and processing
- Windows WASAPI for loopback capture

---

## 📁 Essential Files (Read These)

| File | Priority | Why It Matters |
|------|-----------|----------------|
| `Hearbud/RecorderEngine.cs` | 🔴 CRITICAL | All audio pipeline logic (~1065 lines) |
| `Hearbud/MainWindow.cs` | 🟡 Important | UI logic, event handlers |
| `Hearbud/AppSettings.cs` | 🟢 Reference | Settings persistence |
| `docs/ARCHITECTURE.md` | 🔴 CRITICAL | System architecture and design decisions |
| `docs/LLM_QUICK_REFERENCE.md` | 🟡 Important | Condensed reference, quick lookup |
| `docs/WORKING_WITH_AUDIO.md` | 🟡 Important | Audio/DSP theory, WASAPI specifics |

---

## 🎯 Key Concepts

### 1. Threading Model (Don't Break This!)
```
UI Thread (WPF)           → User input, meters (100ms)
    Audio Callback Threads   → WASAPI callbacks (~100 Hz)
        ↑                         ↑
        │                         │
        └─ MUST NOT BLOCK ────────┘
Disk Writer Thread        → Async I/O via BlockingCollection
```

**Golden Rule:** Audio callbacks (`OnLoopbackData`, `OnMicData`) complete in <1ms. No allocations, no I/O, no blocking.

### 2. Audio Flow
```
System Audio (Loopback)              Microphone
      ↓                                    ↓
OnLoopbackData()                    OnMicData()
      ↓                                    ↓
  Metering → Queue Write            Ring Buffer → Queue Write
      ↓                                    ↓
                   Mix (system + mic * 0.5)
                            ↓
                      Async Disk Write
```

**Clock Sync:** Loopback is master, mic is slave (buffered and pulled to match).

### 3. Output Files
- `*-system.wav` - 16-bit PCM, raw system
- `*-mic.wav` - 16-bit PCM, raw mic (converted to match loopback)
- `*-mix.wav` - 32-bit PCM, mixed with gains and soft clip
- `*.mp3` - Encoded mix (optional)

---

## ⚠️ Common Pitfalls (Watch Out!)

| Mistake | Symptom | Fix |
|---------|---------|-----|
| `new byte[]` in audio callback | Memory growth, GC stutter | Use `ArrayPool<byte>.Shared.Rent()` |
| File I/O in callback | Dropouts, glitches | Queue to `_writeQueue` instead |
| Long `lock(_micRingLock)` | Audio gaps | Keep lock minimal, avoid allocs |
| Not returning pooled buffers | Memory leak | `ArrayPool.Return()` in `finally` |
| Modifying gain during recording | Already safe | Gain properties are runtime-adjustable |
| Forgetting `FillWithZeros = true` | Callbacks stop when silent | Set in `SoundInSource` initialization |

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
- Ring buffer: ~384 KB (4 sec @ 48kHz stereo)
- Temporary float buffers: ~64 KB each
- Disk write buffers: Pooled per block

### Callback Frequency
- Audio callbacks: ~100-150 Hz
- UI meter timer: 100 ms (10 Hz)
- Diagnistic logging: Every 50 loopback blocks

---

## 🔎 Quick Debugging

### Audio Callback Not Firing
- Check device is actually active (not muted/disabled)
- Verify `_loopCap.Start()` / `_micCap.Start()` called
- Look at `Windows Audio Service` (services.msc)

### Memory Leak
- Profile Diagnostic Tools (VM, GC heap size)
- Check `ArrayPool.Return()` usage
- Verify `Dispose()` called on all audio objects

### Poor Audio Quality
- Soft clip active? (tanh prevents harsh distortion)
- Dither correct? (TPDF for 16-bit conversion)
- Sample rates match? (mic resampled to loopback)

### Sync Issues
- Check session log `Mic ring backlog: X s`
- ~0.1s = healthy
- ~0.0s = underruns
- >0.2s = overruns

---

## 📚 Deep Dive Resources

- **Full Architecture:** `docs/ARCHITECTURE.md`
- **Audio Math:** `docs/WORKING_WITH_AUDIO.md`
- **Dev Workflow:** `docs/CONTRIBUTING.md`
- **Troubleshooting:** `docs/TROUBLESHOOTING.md`
- **Quick Reference:** `docs/LLM_QUICK_REFERENCE.md`

---

## ✅ Health Check Before Pushing

1. ✅ Audio callbacks non-blocking (no `new`, no I/O)
2. ✅ `ArrayPool.Return()` called for all rented buffers
3. ✅ `IDisposable` pattern implemented on new audio resources
4. ✅ Session logs helpful (device info, diagnostics)
5. ✅ Settings migration handled (if changed)
6. ✅ No `Thread.Sleep()`, `await` in callbacks
7. ✅ Ring buffer lock minimal
8. ✅ Dispose idempotent (`if (_disposed) return;`)

---

## 💡 Tips from the Original Codebase

1. **Defensive Coding:** Almost every resource disposal wrapped in try/catch
2. **Logging Pervasive:** `Info()`, `Warn()`, `Error()` throughout for crash analysis
3. **Performance First:** Zero allocations in audio hot path
4. **User Experience:** Throttled UI updates, clear error messages
5. **Async By Default:** Any I/O is offloaded to background threads

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
