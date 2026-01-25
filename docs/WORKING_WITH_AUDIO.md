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

**Hearbud's Approach:** The sample rate is determined by the loopback device's native rate. The microphone is automatically resampled to match this rate.

### Bit Depth

The number of bits per audio sample:
- **16-bit** (PCM): Standard, 65,536 possible values (-32,768 to +32,767). Dynamic range: ~96 dB
- **24-bit** (PCM): Professional, 16,777,216 possible values. Dynamic range: ~144 dB
- **32-bit float**: Standard for processing. Range: ±1.0 (normalized), unlimited headroom

**Hearbud's Output:**
- System/Mic WAVs: 16-bit PCM (standard, widely compatible)
- Mix WAV: 32-bit float (preserves headroom before MP3 encoding)

### Channels

Hearbud primarily uses stereo (2 channels):
- Channel 0: Left (L)
- Channel 1: Right (R)

**Mic Channel Handling:**
- Mono mic (1 channel) is copied to both L and R in stereo output
- Stereo mic passes through with possible channel reduction if loopback is mono

### PCM Data

Audio is represented as **Pulse Code Modulation** (PCM) - a raw sequence of sample values.

**Formats:**
- **16-bit PCM:** Int16 per sample, signed little-endian
- **32-bit Float:** Float32 per sample, IEEE 754, normalized to ±1.0

**Conversion in Hearbud:** CSCore handles PCM→_FLOAT conversion via `ToSampleSource()`.

---

## WASAPI (Windows Audio Session API)

### Overview

WASAPI is the Windows API for low-latency audio capture and playback. Hearbud uses two WASAPI modes:

1. **WasapiLoopbackCapture:** Captures what the system plays to a speaker endpoint
2. **WasapiCapture:** Captures from a microphone endpoint

### Loopback Capture Explained

Loopback capture is a special mode where you capture audio **after** it's been mixed by Windows for output to a speaker. This allows recording:
- System audio (music, videos, games)
- Virtual audio sources (DAW outputs, routing software)
- Mixed application outputs (multiple apps playing simultaneously)

**Important:** Loopback only captures what's playing through a specific physical or virtual output device. If an app is muted or routed elsewhere, it won't be captured.

### Device Selection via CSCore

CSCore wraps WASAPI endpoints with the `MMDevice` class:

```csharp
using var enumerator = new MMDeviceEnumerator();

// Get specific device
MMDevice device = enumerator.GetDevice(deviceId);

// Enumerate devices
foreach (var dev in enumerator.EnumAudioEndpoints(DataFlow.Capture, DeviceState.Active))
{
    // dev.FriendlyName: Human-readable name (e.g., "Realtek High Definition Audio")
    // dev.DeviceID: Persistent unique identifier
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
var loopCap = new WasapiLoopbackCapture { Device = device };
loopCap.Initialize();  // Opens device stream

// Wrap with CSCore helpers
var loopIn = new SoundInSource(loopCap) { FillWithZeros = true };
var loopSrc = loopIn.ToSampleSource();  // Convert PCM to float samples

// Subscribe to data callback
loopIn.DataAvailable += OnLoopbackData;
```

**Key Parameters:**
- **FillWithZeros=true:** Ensures callback always fires even if device goes silent
- **WaveFormat:** After `Initialize()`, `loopCap.WaveFormat` reveals format (rate, channels, bit depth)

### Data Available Callback Structure

```csharp
void OnLoopbackData(object? sender, DataAvailableEventArgs e)
{
    // e.ByteCount: Number of bytes in this block
    // Data is in PCM format matching WaveFormat
}
```

**Typical Block Sizes:**
- 44.1 kHz stereo @ 16-bit: ~8-10 kB per event
- 48 kHz stereo @ float32: ~16-20 kB per event
- Event frequency: ~100-150 times per second

**Critical Rule:** Complete all work in this callback **before the next callback**, or you'll get glitches. Hearbud uses async I/O for disk writes to avoid blocking.

---

## Digital Signal Processing (DSP) in Hearbud

### Signal Flow

```
Raw PCM (WASAPI)
    ↓
Float Samples (CSCore ToSampleSource)
    ↓
ConvertToTarget (resample/remix to common format)
    ↓
Gain Application (multiply by MicGain or LoopGain)
    ↓
Mixing: s + m * 0.5 (average of system + mic)
    ↓
Soft Clipping (tanh)
    ↓
PCM Conversion (FloatToPcm16 or FloatToPcm32)
    ↓
File Write (Async)
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

### Soft Clipping (tanh)

Tan hyperbolic (tanh) provides natural non-linear limiting:

```
x = tanh(x)  // Continuous non-linear curve for entire signal
```

**Math:**

```
tanh(x) = (e^x - e^(-x)) / (e^x + e^(-x))
```

**Graph Behavior:**
- Linear-ish for |x| < 0.5
- Begins limiting around |x| = 1.0
- Asymptotically approaches ±1.0 as |x| → ∞
- **Smooth, continuous transition** at all amplitude levels

**Why Soft Clip?**
- Hard clipping (clamp) produces harsh, digital distortion
- tanh is similar to analog tape/electronic limiting (more musical)
- Preserves transients while controlling peaks
- **Applied consistently to entire signal** to prevent audible "pops" or clicks caused by discontinuities

---

## Resampling and Format Conversion

### The Problem: Mismatched Formats

Microphone and loopback devices may have different formats:
- Different sample rates (44.1 kHz vs 48 kHz)
- Different channel counts (mono vs stereo)
- Different bit depths (16-bit vs 32-bit float)

**Solution:** Hearbud converts all mic audio to match loopback format.

### Linear Interpolation Resampling

Hearbud implements simple linear interpolation for sample rate conversion:

```
source_frame_index = destination_frame * (source_rate / dest_rate)
fractional_part = source_frame_index - floor(source_frame_index)
interpolated = sample[floor_index] * (1 - fractional_part) + sample[floor_index + 1] * fractional_part
```

**Code Implementation:**
```csharp
float ratio = (float)srcRate / dstRate;
int outputFrame = f;
float inputPos = f * ratio;
int i0 = (int)inputPos;
int i1 = Math.Min(i0 + 1, srcFrames - 1);
float t = inputPos - i0;

for (int c = 0; c < srcChannels; c++)
{
    float s0 = src[i0 * srcCh + c];
    float s1 = src[i1 * srcCh + c];
    output[outputIndex++] = s0 + (s1 - s0) * t;
}
```

**Trade-offs:**
- [✓] Fast, minimal CPU overhead
- [✓] Preserves low frequencies well
- [✗] Anti-aliasing not implemented (minor high-freq artifacts, acceptable for speech)
- [✗] No phase correction (minor timing shifts)

**Better Resamplers (not implemented):**
- Cubic/spline interpolation (smoother)
- Sinc-based resampling (highest quality, high CPU cost)
- Polyphase implementations (efficient high-quality)

### Channel Conversion

**Mono to Stereo (1 → 2):**
```csharp
// Duplicate mono to both channels
outputL = inputMono;
outputR = inputMono;
```

**Stereo to Mono (2 → 1):**
```csharp
// Average (equal power)
outputMono = 0.5 * (inputL + inputR);
```

**Stereo to 5.1 Surround (not supported):**
- Requires panning rules, not implemented in Hearbud

### Zero-Allocation Resampling

**Before (allocating each call):**
```csharp
float[] temp = new float[dstFrames * srcCh];  // Allocates!
```

**After (pooling):**
```csharp
// Caller provides scratch buffer
ref float[] scratch
EnsureCapacity(ref scratch, dstFrames * srcCh);  // Reuse or resize
temp = scratch;  // Use the reusable buffer
```

**Why This Matters:**
- Resampling called ~100 times/sec
- 48 kHz stereo → 44.1 kHz stereo: ~10,000 samples/block
- Allocating 80 KB per block = 8 MB/sec allocation = GC pressure disasters

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

### Hearbud's Implementation

```csharp
// ThreadLocal RNG avoids lock contention
private static readonly ThreadLocal<Random> _rng =
    new ThreadLocal<Random>(() => new Random(unchecked(Environment.TickCount * 31 + Thread.CurrentThread.ManagedThreadId)));

private static void FloatToPcm16(float[] src, byte[] dst, int count)
{
    var rng = _rng.Value!;
    for (int i = 0; i < count; i++)
    {
        float v = src[i];
        if (v > 1f) v = 1f; else if (v < -1f) v = -1f;

        float scaled = v * 32767.0f;

        // TPDF dither: two randoms subtracted
        float dither = (float)rng.NextDouble() - (float)rng.NextDouble();
        float withDither = scaled + dither;

        int s = (int)MathF.Round(withDither);
        // ... clamp and write to dst
    }
}
```

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

- **32-bit float output:** No quantization, no dither needed
- **Already-quantized audio:** Don't re-dither already-dithered audio cascades noise
- **Hearbud's Choice:** Dither applied only to 16-bit system/mic WAVs, not 32-bit mix

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

### Hearbud's Solution: Loopback as Clock

**Design Choice:** Loopback is the master clock. Mic is a slave.

**Implementation:**
1. Mic samples go into a ring buffer as they arrive
2. Loopback callback pulls mic samples from ring buffer to match
3. If mic is slow, gaps are zero-filled (silence preserved)
4. If mic is fast, ring buffer overruns, dropping oldest samples (newest kept)

**Why This Works:**
- Final output's timing matches system audio (what you see on screen)
- Mic is secondary source, acceptable to drop/realign

### Drift Compensation (Not Implemented)

Advanced systems use phase-locked loops (PLL) or sample rate converters (SRC) to keep sources synchronized:

```
mic_rate_adjustment = 1.0 + PID_controller(ring_buffer_ideal_depth - ring_buffer_actual_depth)
adjusted_mic_samples = resample(mic_raw, mic_rate_adjustment)
```

**Hearbud's Simpler Approach:**
- Small ring buffer (4 sec at 48 kHz) absorbs drift
- Overruns/underruns happen but are brief and perceptually minimal
- Trade-off: Complexity vs. simplicity for typical use cases

### Session Log Diagnostics

Hearbud logs sync issues in `*.txt` session log:

```
[2025-12-23 14:30:00.123] INFO OnLoopbackData: Mic ring backlog: 0.0523 s (max 0.1200 s)
[2025-12-23 14:30:05.456] INFO OnLoopbackData: Mic ring backlog: 0.0487 s (max 0.1200 s)
```

**Interpretation:**
- `backlog`: Current mic buffer depth in seconds (lower = underrun risk, higher = overrun risk)
- `max backlog`: Peak observed (useful for ring buffer sizing)
- Stable backlog ~0.1s = good sync
- Consistent 0.0s backlog = underruns (mic is too slow)
- Consistent near-max backlog = overruns (mic is too fast)

### When Loopback is Silent

If system audio stops (e.g., user speaks without screen audio):

1. Loopback callback stops firing
2. `_lastLoopTick` ages beyond `LoopSilentMsThreshold` (200ms)
3. Mic callback detects "loop silent"
4. Mic **drives the output alone**:
   - System WAV: Write zeros (silence)
   - Mic WAV: Write mic audio
   - Mix WAV: Write mic audio (0.0 + mic)

When loopback **resumes**:
1. Ring buffer is **cleared** (`_micR = _micW; _micCount = 0`)
2. Old mic backlog is **dropped**
3. Sync restarts cleanly

**Critical Avoidance:** Without clearing the ring buffer, old mic audio would play over new system audio (echo artifact).

---

## Quick Reference: Audio Math Cheatsheet

| Operation | Formula | Notes |
|-----------|---------|-------|
| Linear to dB (amplitude) | `dB = 20 * log10(amplitude)` | Voltage/amplitude, not power |
| dB to Linear (amplitude) | `amp = 10^(dB / 20)` | |
| Power to dB | `dB = 10 * log10(power)` | Power, not voltage |
| Two-source average mix | `out = (s1 + s2) * 0.5` | Preserves headroom |
| RMS calculation | `sqrt(sum(x^2) / N)` | N samples in interval |
| Soft clip (tanh) | `tanh(x)` or `x / sqrt(1 + x^2)` | Many approximations exist |
| Resampling ratio | `src_rate / dst_rate` | Multiplier for index calculations |

---

## Further Reading

- **"The Audio Programming Book"** by Richard Boulanger & Victor Lazzarini
- **"Practical Signal Processing"** by Mark Owen (Chapter 1: Digital Audio Basics)
- **"Mastering Audio: The Art and the Science"** by Bob Katz (Chapter 2: Monitoring)
- **WASAPI Documentation:** [Microsoft Docs](https://docs.microsoft.com/en-us/windows/win32/coreaudio/wasapi)
- **Bob Katz's K-System Metering:** Alternative to peak/RMS, not used in Hearbud
