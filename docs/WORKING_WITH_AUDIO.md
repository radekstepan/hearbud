# Working with Audio in Hearbud

This document dives deep into audio-related concepts, WASAPI specifics, and the DSP (Digital Signal Processing) math used in Hearbud. It's essential reading for developers modifying the audio pipeline.

## Table of Contents
- [Audio Fundamentals](#audio-fundamentals)
- [WASAPI (Windows Audio Session API)](#wasapi-windows-audio-session-api)
- [Understanding Loopback Capture](#understanding-loopback-capture)
- [Digital Signal Processing (DSP) in Hearbud](#digital-signal-processing-dsp-in-hearbud)
- [Resampling and Format Conversion](#resampling-and-format-conversion)
- [Metering and dBFS Calculations](#metering-and-dbfs-calculations)
- [Dithering Theory and Implementation](#dithering-theory-and-implementation)
- [SMPTE Time Synchronization Considerations](#smpte-time-synchronization-considerations)

---

## Audio Fundamentals

### Sample Rate

The number of samples taken per second, measured in Hz. Common sample rates:
- 44.1 kHz (CD quality)
- 48 kHz (Professional audio standard, default in Hearbud)
- 96 kHz (High-resolution)
- 192 kHz (Ultra high-resolution)

**Hearbud's Approach:** Fixed 48 kHz stereo float32 everywhere — capture requests
this format from both legs (WASAPI shared mode converts from any native device
format), so nothing is ever resampled wholesale.

### Bit Depth

The number of bits per audio sample:
- **16-bit** (PCM): Standard, 65,536 possible values (-32,768 to +32,767). Dynamic range: ~96 dB
- **24-bit** (PCM): Professional, 16,777,216 possible values. Dynamic range: ~144 dB
- **32-bit float**: Standard for processing. Range: ±1.0 (normalized), unlimited headroom

**Hearbud's Output:**
- System/Mic/Mix WAVs: 32-bit IEEE float RF64 (one format, no 4 GB wall)
- MP3: encoded from the float mix via float → samples → clipped 16-bit

### Channels

Hearbud primarily uses stereo (2 channels):
- Channel 0: Left (L)
- Channel 1: Right (R)

**Mic Channel Handling:**
- Capture requests stereo from every device; the WASAPI shared-mode engine
  upmixes mono mics and converts channel counts automatically
- No channel conversion code exists in Hearbud (deleted in Phase 4)

### PCM Data

Audio is represented as **Pulse Code Modulation** (PCM) - a raw sequence of sample values.

**Formats:**
- **16-bit PCM:** Int16 per sample, signed little-endian
- **32-bit Float:** Float32 per sample, IEEE 754, normalized to ±1.0

**Conversion in Hearbud:** None needed — NAudio captures float32 directly and the
pipeline stays float32 end to end (only the MP3 step converts to 16-bit).

---

## WASAPI (Windows Audio Session API)

### Overview

WASAPI is the Windows API for low-latency audio capture and playback. Hearbud uses NAudio's `WasapiRecorder` for both legs:

1. **Loopback leg:** `WasapiRecorder` + `WithLoopbackCapture()` on a render endpoint
2. **Mic leg:** `WasapiRecorder` (+ `WithRawMode()`, cooked fallback) on a capture endpoint

Both request 48 kHz float32 stereo with MMCSS "Pro Audio" priority and IAudioClient3 low-latency mode.

### Loopback Capture Explained

Loopback capture is a special mode where you capture audio **after** it's been mixed by Windows for output to a speaker. This allows recording:
- System audio (music, videos, games)
- Virtual audio sources (DAW outputs, routing software)
- Mixed application outputs (multiple apps playing simultaneously)

**Important:** Loopback only captures what's playing through a specific physical or virtual output device. If an app is muted or routed elsewhere, it won't be captured.

### Device Selection via NAudio

NAudio wraps WASAPI endpoints with the `MMDevice` class (the engine owns all of
them; the UI only sees `AudioDeviceInfo` DTOs):

```csharp
using var enumerator = new MMDeviceEnumerator();

// Get specific device
MMDevice device = enumerator.GetDevice(deviceId);

// Enumerate devices
foreach (var dev in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
{
    // dev.FriendlyName: Human-readable name (e.g., "Realtek High Definition Audio")
    // dev.ID: Persistent unique identifier
    // dev.State: Active, Disabled, NotPresent, Unplugged
}
```

### Default Device Roles

WASAPI devices have roles for default selection:

```csharp
// Get default communications device (usually headset/mic)
var defMic = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);

// Get default multimedia device (usually speakers)
var defSpeaker = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
```

### Initializing WASAPI Capture

```csharp
var target = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
var loop = new WasapiRecorderBuilder()
    .WithDevice(device)          // retained MMDevice — must outlive recording
    .WithLoopbackCapture()
    .WithFormat(target)          // shared-mode engine converts any native format
    .WithLowLatency(true)        // IAudioClient3 (NAudio falls back gracefully)
    .WithMmcssThreadPriority("Pro Audio")
    .Build();

loop.DataAvailable += OnLoopData;      // zero-copy span + QPC stamp
loop.RecordingStopped += OnLoopStopped; // error path → per-leg auto-rebuild
loop.StartRecording();
```

**Key Parameters:**
- **Shared mode (default):** The audio engine converts device-native formats to the requested float32 stereo — no conversion code in Hearbud
- **Raw mode (mic only):** Bypasses vendor APOs; falls back to cooked capture when the driver rejects it

### Data Available Callback Structure

```csharp
// buffer: ReadOnlySpan<byte> over the WASAPI buffer (valid during the callback only)
// flags: AudioClientBufferFlags (Silent => treat as zeros)
// devicePosition: device frame counter (gap detection)
// qpcPosition: capture instant in 100-ns units (NOT Stopwatch ticks!)
void OnLoopData(ReadOnlySpan<byte> buffer, AudioClientBufferFlags flags,
    long devicePosition, long qpcPosition)
{
    // copy → stamp → channel TryWrite. Nothing else.
}
```

**Typical Block Sizes:**
- 48 kHz stereo @ float32: ~4-16 kB per event (10 ms ≈ 3.8 kB)
- Event frequency: ~100 times per second per leg

**Critical Rule:** Copy, stamp, hand off — then return. The mixer thread owns
everything downstream; a throw here kills the capture thread.

---

## Digital Signal Processing (DSP) in Hearbud

### Signal Flow

```
Float32 @ 48 kHz stereo (NAudio capture — no conversion)
    ↓
Bounded channels (drop-oldest handoff to the mixer)
    ↓
TimestampedMicBuffer (1 s horizon + PLL read rate)
    ↓
Gain Application (multiply by MicGain or LoopGain)
    ↓
DC block per leg (~8 Hz high-pass; mix only, raws stay raw)
    ↓
Mixing: s + m * 0.5 (average of system + mic)
    ↓
Kneed soft-limit (transparent below 0.8, ceilinged to ±1)
    ↓
RF64 float32 file write (mixer thread, 2 s header re-patch)
```

### Gain Application

Gain is a linear multiplier applied to each sample:

```
output_sample = input_sample * gain_multiplier
```

**Gain to dB Conversion:**
```
dB = 20 * log10(gain_multiplier)
gain_multiplier = 10^(dB / 20)
```

**Examples in Hearbud:**
- Gain = 1.0: 0 dB (no change)
- Gain = 2.0: +6 dB (double amplitude)
- Gain = 0.5: -6 dB (half amplitude)
- Gain = 3.0: +9.5 dB (max in UI)

### Mixing Algorithm

Hearbud uses a simple average mix:

```
mixed_sample = (system_sample * LoopGain + mic_sample * MicGain) * 0.5
```

**Why * 0.5?**
- Summing two audio sources can exceed ±1.0 (clipping)
- Multiplying by 0.5 ensures headroom before soft clipping
- Equivalent to -6 dB attenuation on each source

**Alternative Mix Formulas:**
```
// Equal power mix (more perceived volume gain)
mixed_sample = (s + m) * 0.707  // 0.707 ≈ sqrt(0.5)

// No headroom preservation (clips often)
mixed_sample = (s + m)

// Weighted mix
mixed_sample = s * 0.7 + m * 0.3  // System louder than mic
```

### Kneed Soft-Limiting

`SoftLimiter` is transparent below the 0.8 knee and smoothly ceilings to ±1:

```
if |x| < 0.8:  y = x
else:          over = |x| - 0.8
               y = sign(x) * min(0.8 + over / (1 + over * 2.2), 1)
```

**Graph Behavior:**
- Bit-identical passthrough for |x| < 0.8 (quiet transients untouched)
- Smooth rounding above the knee — no discontinuity, no "pops"
- Hard ceiling at ±1.0 (e.g. x = 1.0 → ~0.939, x = 10 → 1.0)

**Why a Knee Instead of tanh?**
- Hard clipping (clamp) produces harsh, digital distortion
- Global tanh also dulls quiet signals (it bends the whole curve)
- The knee only touches hot signals — the common case stays pristine

---

## Resampling and Format Conversion

### The Problem Hearbud No Longer Has

Microphone and loopback devices may natively differ (44.1 vs 48 kHz, mono vs
stereo) — but capture requests 48 kHz float32 stereo from both legs and the
WASAPI shared-mode engine converts, so **no wholesale resampling or channel
conversion code exists** (deleted in Phase 4).

### What Remains: ±0.1 % Drift Bending

Independent device crystals drift ~±50 ppm. `TimestampedMicBuffer` absorbs it
with a PLL read rate in [0.999, 1.001] applied through linear interpolation
with a fractional cursor carried across blocks:

```
pos = cursor + output_frame * ratio
i0 = floor(pos); t = pos - i0
interpolated = sample[i0] * (1 - t) + sample[i0 + 1] * t
```

**Trade-offs (for ppm bending only — inaudible):**
- [✓] Fast, minimal CPU overhead
- [✓] No per-block rounding jitter (fractional cursor persists)
- [~] Linear interp aliases ultrasonics — irrelevant at ±0.1 % rate bending
  (the old sin was linear for wholesale 44.1→48 kHz conversion, long gone)

**Better Resamplers (deliberately not wired):**
- NAudio's `WdlResampler` would drop into the inner loop unchanged in shape —
  kept out because the benefit is below measurement for drift correction and
  it can't be listening-tested here (see ADR-0012)

---

## Metering and dBFS Calculations

### Peak Level

Peak = the maximum absolute sample value in a measurement interval:

```
peak = max(|sample[i]|) for all samples in interval
```

**Range:** 0.0 (silence) to 1.0 (full scale)

### RMS (Root Mean Square)

RMS measures average power:

```
RMS = sqrt((sum(|sample[i]|^2)) / N)
```

**Where:**
- N = number of samples in interval
- |sample[i]| = absolute value of sample i
- sum = sum of squares

**Perceptual Meaning:** RMS approximates perceived loudness better than peak.

### dBFS (Decibels Full Scale)

Logarithmic representation relative to full scale:

```
dBFS = 20 * log10(linear_value)
```

**Common Reference Points:**
- 0 dBFS = 1.0 (full scale, maximum digital audio)
- -6 dBFS = 0.5 (half amplitude)
- -12 dBFS = 0.25 (quarter amplitude)
- -60 dBFS ≈ 0.001 (near silence; practical floor in Hearbud)

**Code (from `Dbfs.cs`):**
```csharp
public static double ToDbfs(double peakLin)
{
    if (peakLin <= 1e-6) return -60.0;  // Avoid log(0) issues
    var d = 20.0 * Math.Log10(peakLin);
    return Math.Max(-60.0, d);  // Clamp to -60 dB floor
}
```

### Throttled Meter Updates

Sending meter updates every audio callback is wasteful:

**Problem:**
- ~100 callbacks/sec
- UI cross-thread calls 100 times/sec = slow responsiveness
- Human eyes can't perceive >20-30 Hz updates

**Solution:** Throttle to ~50 ms intervals (~20 Hz):

```csharp
msSince = (nowTicks - _lastLevelTickMic) * _tickMs;
if (msSince >= LevelThrottleMs && _rmsCountSinceLastMic > 0)
{
    float rms = Math.Sqrt(_rmsSumSinceLastMic / _rmsCountSinceLastMic);
    LevelChanged?.Invoke(this, new LevelChangedEventArgs(...));
    // Reset accumulators
    _peakSinceLastMic = 0f;
    _rmsSumSinceLastMic = 0.0;
    _rmsCountSinceLastMic = 0;
    _lastLevelTickMic = nowTicks;
}
```

### Clip Detection

A clipped sample exceeds ±1.0 (before hard clamping):

```csharp
float abs = MathF.Abs(sample);
if (abs > 1.0) clipped = true;
```

Hearbud tracks clipping **per interval** (reset after each update):
- Flash UI red indicator if any sample clipped in last 50 ms
- Helps user identify gain issues

---

## Dithering Theory and Implementation

### What is Dithering?

Dithering is adding low-level noise before quantization to reduce quantization distortion artifacts.

### The Problem: Quantization Noise

When converting float to 16-bit:
- Float has infinite precision (theoretically)
- 16-bit only has 65,536 steps
- Quiet signals below 1 LSB (1/32,768) get rounded to zero
- Result: harmonic distortion, "brittle" sound on fades/decays

### TPDF (Triangular Probability Density Function) Dither

TPDF is optimal for audio because:
- Eliminates **quantization distortion** (harmonic artifacts)
- Converts distortion to **white noise** (less objectionable)
- Maintains **bit-perfect accuracy** on DC signals

**Algorithm:**
```
ditherValue = (random(0, 1) - random(0, 1))  // Range: -1.0 to +1.0
```

**Why TPDF is Triangular:**
- Subtracting two uniform random values creates triangular distribution
- PDF at value x = 1 - |x| for -1 ≤ x ≤ 1
- Peak at 0, tails off linearly to 0 at ±1

### Hearbud's Implementation: None (Float Pipeline)

The pipeline is float32 end to end, so nothing quantizes and nothing dithers
(TPDF code was deleted in Phase 4). The only int conversion left is the MP3
post-step, which uses NAudio's `SampleToWaveProvider16` (clipping, no dither)
— inaudible under MP3's own psychoacoustic quantization.

### Visualizing Dither Effect

**Without Dither:**
```
Input: 0.5, 0.51, 0.52, 0.53, 0.54
16-bit: 16384, 16384, 16834, 16834, 17168  <- Steps visible
Audio: Fading signal sounds "choppy"
```

**With TPDF Dither:**
```
Input: 0.5, 0.51, 0.52, 0.53, 0.54
Dithered: 16384+5, 16384-3, 16834+1, 16834+7, 17168-2
16-bit: 16389, 16381, 16835, 16841, 17166
Audio: Smooth, noise floor increased slightly, no harmonic distortion
```

### When NOT to Dither

- **32-bit float output:** No quantization, no dither needed (all Hearbud masters)
- **Already-quantized audio:** Don't re-dither already-dithered audio cascades noise

---

## SMPTE Time Synchronization Considerations

### The Challenge

Microphone and system audio come from independent devices with independent clocks:

- **Mic clock:** Microphone's ADC sample clock
- **Loopback clock:** Speaker/audio interface DAC sample clock

Even at the rated 48 kHz, actual rates may be:
- Mic: 48,000.1 samples/sec (slightly fast)
- Loopback: 47,999.8 samples/sec (slightly slow)

**Result:** Drift of ~0.0004% ≈ 2 samples per second. Over 1 hour: 7,200 samples ≈ 150 ms drift!

### Hearbud's Solution: Loopback-Paced Mixer + Timestamps

**Design Choice:** Loopback paces the session; mic is aligned by QPC timestamp.

**Implementation (`TimestampedMicBuffer`):**
1. Mic blocks arrive stamped with 100-ns QPC ticks
2. Each mixer tick reads the mic at (loopNow − 120 ms target latency)
3. If mic is short, gaps are zero-filled (silence preserved, underruns counted)
4. Mic older than 1 s is dropped by timestamp horizon (no stale-mic echo)

**Why This Works:**
- Final output's timing matches system audio (what you see on screen)
- Alignment is by wall clock, not by counting chunks — immune to creep

### Drift Compensation (PLL, Implemented)

A phase-locked loop servos mic depth toward 120 ms by bending the read rate:

```
filtered_err += (depth_ms - 120 - filtered_err) * 0.002
ratio = clamp(1 + filtered_err * 0.00002, 0.999, 1.001)
```

The ratio applies through a fractional read cursor (linear interpolation, no
per-block rounding jitter). Heavy filtering (α = 0.002) matches the physics:
crystals drift over minutes, so the servo must ignore packet jitter.

### Session Log Diagnostics

Hearbud logs sync state in `*.txt` session log (every 50 mixer blocks):

```
[2025-12-23 14:30:00.123] INFO AfterBlock: Mic backlog: 0.0523 s (max 0.1200 s)
[2025-12-23 14:30:05.456] INFO AfterBlock: Mic backlog: 0.0487 s (max 0.1200 s)
```

**Interpretation:**
- `backlog`: Current mic buffer depth in seconds (lower = underrun risk, higher = overrun risk)
- `max backlog`: Peak observed
- Stable backlog ~0.12 s = PLL locked, good sync
- Consistent 0.0 s backlog = underruns (mic is too slow or missing)
- Growing backlog = overruns (mic much faster than loopback)

### When Loopback is Silent

If system audio stops (e.g., user speaks without screen audio):

1. No loopback blocks arrive, so the loopback FIFO stays empty
2. The mixer falls back to steady 10 ms mic-only pulls:
   - System WAV: Write zeros (silence)
   - Mic WAV: Write mic audio
   - Mix WAV: Write mic audio (0.0 + mic)
3. If the mic is also dry, the tick is skipped (session pauses — no silence files)

When loopback **resumes**, mic older than the 1 s horizon is dropped by
timestamp and sync continues — no echo artifact, no mode branches.

---

## Quick Reference: Audio Math Cheatsheet

| Operation | Formula | Notes |
|-----------|---------|-------|
| Linear to dB (amplitude) | `dB = 20 * log10(amplitude)` | Voltage/amplitude, not power |
| dB to Linear (amplitude) | `amp = 10^(dB / 20)` | |
| Power to dB | `dB = 10 * log10(power)` | Power, not voltage |
| Two-source average mix | `out = (s1 + s2) * 0.5` | Preserves headroom |
| RMS calculation | `sqrt(sum(x^2) / N)` | N samples in interval |
| Kneed limit | `x < 0.8 ? x : 0.8 + over/(1+over*2.2)` | Transparent below knee |
| PLL read ratio | `clamp(1 + err*2e-5, .999, 1.001)` | Drift servo, heavily filtered |

---

## Further Reading

- **"The Audio Programming Book"** by Richard Boulanger & Victor Lazzarini
- **"Practical Signal Processing"** by Mark Owen (Chapter 1: Digital Audio Basics)
- **"Mastering Audio: The Art and the Science"** by Bob Katz (Chapter 2: Monitoring)
- **WASAPI Documentation:** [Microsoft Docs](https://docs.microsoft.com/en-us/windows/win32/coreaudio/wasapi)
- **Bob Katz's K-System Metering:** Alternative to peak/RMS, not used in Hearbud
