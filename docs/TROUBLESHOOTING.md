# Troubleshooting Guide

This document covers common issues, error messages, and solutions for Hearbud users and developers.

## Table of Contents
- [Installation and Startup Issues](#installation-and-startup-issues)
- [Audio Device Issues](#audio-device-issues)
- [Recording Problems](#recording-problems)
- [Output File Issues](#output-file-issues)
- [Performance and Glitches](#performance-and-glitches)
- [MP3 Encoding Issues](#mp3-encoding-issues)
- [Crash and Log Analysis](#crash-and-log-analysis)
- [Developer-Specific Issues](#developer-specific-issues)

---

## Installation and Startup Issues

### Application Won't Start

**Symptoms:**
- Double-clicking `Hearbud.exe` does nothing
- Error dialog appears immediately
- Process shows up in Task Manager but no UI

**Causes and Solutions:**

#### Missing .NET Runtime
```
Error: The application to execute does not have a .NET Desktop Runtime installed
```

**Solution:**
- Install .NET 9 Desktop Runtime from [dotnet.microsoft.com](https://dotnet.microsoft.com/download/dotnet/9.0)
- Or use the **self-contained** build (includes runtime)

#### Antivirus Blocking
Antivirus may flag self-contained builds falsely due to packed executables.

**Solution:**
- Add Hearbud to antivirus exclusions
- Use the non-self-contained build with .NET installed

#### Corrupted Installation

**Solution:**
1. Delete Hearbud folder
2. Download fresh copy
3. Verify SHA-256 checksum if provided

### Settings Not Persisting

**Symptoms:**
- Folder path resets on restart
- Gains default to 1.0x
- Device selection lost

**Cause:** Settings file not writable

**Solutions:**

#### Check Config File Location
- Path: `%UserProfile%\.hearbud_config.json`
- Example: `C:\Users\YourName\.hearbud_config.json`

#### Verify Write Permissions
```powershell
# Check file exists and is writable
Test-Path $env:USERPROFILE\.hearbud_config.json
Get-ItemProperty $env:USERPROFILE\.hearbud_config.json | Select-Object IsReadOnly
```

If `IsReadOnly` is `True`, remove the read-only flag or delete the file.

#### Disk Full or Protected Directory

**Solution:**
- Ensure user profile directory has space
- Some enterprise setups restrict file writes; use `%LocalAppData%` alternative (requires code change)

---

## Audio Device Issues

### Devices Not Showing in Dropdowns

**Symptoms:**
- Mic dropdown empty
- Speaker dropdown empty
- Refresh button has no effect

**Causes and Solutions:**

#### WASAPI Not Available

**Diagnosis:** Check Windows Audio Service running:
```powershell
Get-Service -Name "AudioSrv"
```

**Solution:**
1. Open `services.msc`
2. Locate "Windows Audio" service
3. Ensure status is "Running"
4. If stopped, right-click → Start

#### Disabled/Unplugged Devices

**Diagnosis:**
1. Open "Sound Settings" in Windows
2. Click "More sound settings"
3. Right-click empty area → "Show Disabled Devices"
4. Check if devices are disabled

**Solution:**
- Right-click disabled device → Enable
- If device shows "Not Present" (unplugged), reconnect hardware

#### Privacy Settings Blocking Mic Access

**Diagnosis (Windows 10/11):**
1. Settings → Privacy → Microphone
2. Check "Allow apps to access your microphone" is On

**Solution:**
- Enable microphone access
- Add Hearbud to allowed apps if prompted

### Wrong Device Selected or Auto-detection Fails

**Symptom:** Default device not selected on startup

**Cause:** Windows default device changed between sessions

**Solution:**
- Manually select correct device in dropdowns
- Refresh devices button re-queries defaults

### Device Disappears During Recording

**Symptom:** USB device unplugged mid-take; the affected leg drops out

**Diagnosis:** Check session log (`*.txt`):
```
[...] INFO Loopback leg lost — rebuilding…
[...] INFO Loopback leg recovered
```

**Behavior:** Capture legs auto-rebuild with backoff while the mixer coasts on
zeros, so the session survives short disconnects. If a device never returns,
the take continues degraded (loopback-only or mic-only) with a loud status error.

**Solution:**
- Replug promptly; recovery is automatic
- Prefer robust connections (powered USB hub, etc.) for long takes

### Virtual Audio Drivers Not Detected

**Symptom:** Voicemeeter, VB-Audio Cable, etc. not in speaker list

**Causes:**

#### Driver Not Installed or Crashed

**Diagnosis:**
1. Open Device Manager (Win+X → Device Manager)
2. Look under "Sound, video and game controllers"
3. Check for virtual driver (e.g., "VB-Audio Virtual Cable")

**Solution:**
- Reinstall virtual driver software
- Restart computer

#### Wrong Data Flow Type

**Diagnosis:** Virtual drivers can be input or output; ensure the one you want is in correct category.

**Example:**
- **Loopback capture:** Needs a **render** (output) device (what Windows plays into)
- **Mic capture:** Needs a **capture** (input) device

**Solution:**
- Select loopback device as the virtual output (render) endpoint
- Virtual input used in DAW/routing software, not typically what you select in Hearbud

---

## Recording Problems

### Nothing Recorded (Silent Files)

**Symptom:** All WAV files are silent (0 bytes or near-zero content)

**Causes and Solutions:**

#### Devices Not Actually Playing Audio

**Diagnosis:**
- Play music/video with system audio
- Observe meters: If flat at bottom, no loopback capture
- Speak into mic: If mic meter flat, no mic capture

**Solution:**
- Ensure audio device is actually outputting sound (check volume, mute)
- Test with Windows Sound Recorder app to verify microphone works

#### Wrong Device Selected for Loopback

**Diagnosis:** Loopback meter not moving

**Solution:**
- Select correct speaker output as loopback device
- In Windows Sound settings, verify audio is routed to that speaker

#### Exclusive Mode Locking

**Symptom:**
```
[...] ERROR OpenCaptureAsync: System.Runtime.InteropServices.COMException: The audio endpoint cannot be used
```

**Cause:** Another application has exclusive access (e.g., DAW, game)

**Solution:**
1. Open "Sound Settings"
2. Select speaker device → Properties
3. "Advanced" tab
4. **Uncheck** "Allow applications to take exclusive control"
5. Restart Hearbud

#### Driver Issues

**Diagnosis:**
- Hearbud meters work, but Windows apps don't output audio to device
- Or vice versa

**Solution:**
- Update audio drivers from manufacturer website
- Restart Windows Audio service

### Recording Stops Unexpectedly

**Symptom:** Recording stops mid-way without user input

**Causes:**

#### Low Disk Space

**Diagnosis:**
- Check free space on output drive (e.g., `C:\Users\YourName\Music`)
- Minimum: Several hundred MB for 10+ minute recording

**Solution:**
- Free up disk space
- Change output folder to different drive

#### Write Failures (Permission/Network)

**Diagnosis:** Session log shows:
```
[...] ERROR MixerLoop fatal: IOException: The process cannot access the file...
```

**Solution:**
- Ensure output folder is writable
- Avoid network shares with authentication issues
- Try local drive

### Only One Channel Recorded

**Symptom:** Playback only from left or right speaker, or mic only on one side

**Causes:**

#### Mono-to-Stereo Configuration Issue

**Diagnosis:** System audio is stereo, mic is mono

**Solution:**
- Hearbud captures stereo from every device; the WASAPI shared-mode engine upmixes mono mics automatically (no conversion code)
- Check driver settings for mic: Ensure it's not misconfigured as stereo with one channel silent

#### Physical Connection Issue

**Diagnosis:**
- 3.5mm TRS plug not fully inserted (common in external mics)
- Faulty cable

**Solution:**
- Reseat all audio connections
- Test with different cable/mic

### Mismatched Timestamps/Delay Between Sources

**Symptom:** Mic audio appears slightly delayed/advanced relative to system audio

**Cause:** Clock drift between microphone ADC and system DAC

**Normal Behavior:** Small drift (≤50 ms) is expected and compensated by the timestamp buffer + PLL.

**If large delay (>100 ms):**

#### Check Session Log

```text
[...] INFO AfterBlock: Mic backlog: 0.1523 s
```

- `backlog`: ~0.1s is normal sync
- `backlog` consistently near 0: Mic is too slow (underruns)
- `backlog` consistently near max (1 s horizon): Mic is too fast (overruns)

**Workarounds:**
- Record silence-only first to see drift direction
- Adjust sample rate in driver to be closer to loopback
- Use devices from same manufacturer if possible (clocks more synchronized)

### Steady Hum or Buzz in the Mic Take

**Symptom:** A faint, steady buzz heard in the headphones *and* present in the mic take.

**Key fact:** 9 times out of 10 this is **upstream of anything Hearbud does**.
The engine runs one float32 format with ±0.1% drift correction and a kneed
limiter — its failure modes are warble, clicks, and dropouts, not a constant
mains tone. A steady tone is the signature of an electrical / acoustic path.

**Triage flowchart (2 minutes):**

1. **Close Hearbud entirely.** Buzz still there at the same pitch?
   → Yes: electrical or Windows-routing cause (suspects 1–2). Software can't
   record what's already in the analog chain.
2. **Unplug the headphones**, record mic-only. Mic buzz gone?
   → Yes: acoustic crosstalk (suspect 3).
3. Else → capture path (suspect 4): record the proof takes below.

#### Suspect 1 — "Listen to this device" Loop (most likely, software)

- Symptoms: buzz/hiss with nothing playing; mic meter dances when you tap the
  desk; mic gain changes headphone noise.
- Test: Sound settings → mic → Properties → Listen tab. If
  "Listen to this device" is checked, Windows live-routes mic → headphones.
  Uncheck it on **every** input (Stereo Mix, Line In too).
- Note: the engine never enables software monitoring, and mic capture prefers
  raw mode, which bypasses APO monitoring paths (cooked fallback excepted).

#### Suspect 2 — Ground Loop / USB + GPU Whine (very likely, electrical)

- Symptoms: constant 50/60 Hz hum, or a whine that follows GPU load, mouse
  movement, or charger state. Persists with Hearbud closed.
- Tests: unplug the laptop charger (hum drops? ground loop); mute Windows to 0
  (hum stays? it's downstream of the DAC); stress the GPU (pitch follows? coil
  whine); try another USB port / a powered hub for the headset.
- Fix: break the loop — powered hub or USB isolator, one power strip for
  PC+monitor+speakers, ferrite on the headphone cable, or an optical/USB DAC.
- Proof: the hum watchdog is live — while mains hum persists the status line
  shows `Mains hum @ N Hz` and the tray tooltip carries the reading (detector
  trips above −50 dB, `HumWatch.DetectAboveDb`). If it clears after your fix,
  you've proven it.

#### Suspect 3 — Acoustic Crosstalk (likely if open-back / loud)

- Symptoms: "buzz carries through the microphone" — the mic file holds a faint
  copy of headphone output; louder with volume or a lifted earcup.
- Test: 10 s of silence, headphones ON loud; then 10 s unplugged, same gain.
  Null-compare the mic WAVs — buzz vanishing in take 2 means it arrived
  through the air, not the wire.
- Fix: closed-back cans at sane volume, mic closer (less gain = less room),
  push-to-mute monitoring. Per-leg meters make it obvious: system-flat +
  mic-buzzing = acoustic path.

#### Suspect 4 — Capture-Path DSP Artifacts (possible, but sounds different)

- Symptoms of *actual* engine faults: warble, clicks, dull transients,
  dropouts — from drop/stall paths, **not** a steady mains tone. (The old
  wholesale resampler, ring underruns, and global tanh are gone; see
  [ADR-0010](adr/0010-phase2-sync-dsp.md) and
  [ADR-0012](adr/0012-phase4-naudio-rewrite.md).)
- Test: record mic-only and check the session log for drop/underrun counts
  (`HealthReport.DroppedBlocks`, mixer-starved warnings) — those are clicks,
  not hum.

#### Key Diagnostic Partition

WASAPI loopback taps the mix **before the DAC** — headphone buzz physically
cannot enter through the system leg. If `*-system.wav` buzzes, the source app
(or a render-path APO) put it there. Mic-only buzz ⇒ suspects 1–4 above.

---

## Output File Issues

### Files Not Created After Recording

**Symptom:** Stop recording, no files in output folder

**Causes:**

#### Recording Never Started (Monitored Only)

**Diagnosis:**
- Status text shows "Monitoring" not "Recording to WAV..."
- Only Start button (Ctrl+R) begins actual file write

**Solution:**
- Ensure you clicked Record (Ctrl+R), not just selected devices

#### Permission to Write Folder

**Diagnosis:** Try creating a text file manually in output folder

**Solution:**
- Change output folder to `C:\Temp\` or your Desktop
- Ensure user has Write permissions

#### Output Path Invalid Characters

**Diagnosis:** Base filename contains invalid characters (`:`, `\`, `/`, `?`, etc.)

**Solution:**
- Use only alphanumeric characters, spaces, hyphens, underscores

### File Corrupt or Won't Play

**Symptom:** File size > 0, but won't open in player, or cuts off early

**Causes:**

#### Incomplete Write (Recording Stopped Abruptly)

**Symptom:** File header incomplete

**Solution:**
- Hearbud flushes on Stop; if app crashed mid-record, WAV may lack proper header
- Try importing raw data into Audacity (import raw, set 16-bit PCM, 48kHz stereo, Little-endian)

#### MP3 File Not Encoded but Listed

**Symptom:** Only WAV files exist, MP3 file from session log not found

**Diagnosis:** Session log shows:
```
[...] INFO Mix WAV missing or empty; skipping MP3 encode.
```

**Solution:**
- Check if mix.wav exists and has content
- If missing, disk write failed (see [Recording Stops Unexpectedly](#recording-stops-unexpectedly))

#### Media Foundation Not Available

**Symptom:** MP3 encoding fails with a Media Foundation error

**Diagnosis:** Windows Media Foundation codecs missing (rare on Windows 10/11)

**Solution:**
- Windows 10/11: Run "Windows Features" → "Media Features" → ensure checked
- Windows N versions: Install Media Feature Pack for Windows 10/11

### "File (1).wav" Name Instead of Expected

**Symptom:** File named `rec-20250623_143022 (1).wav` instead of `rec-20250623_143022.wav`

**Cause:** File already exists in output directory

**Behavior:** Hearbud auto-increments to avoid overwriting.

**Solution:**
- Delete/rename old files if you don't need them
- Or use timestamp-based base names, unique per session

---

## Performance and Glitches

### Audio Glitches/Dropouts During Recording

**Symptom:** Playback has clicks, pops, stutters, or gaps

**Causes and Solutions:**

#### High CPU Usage

**Diagnosis:** Open Task Manager, check CPU during recording. >80% problematic.

**Solution:**
- Close unnecessary applications
- Reduce system load (web browsers with many tabs, games, etc.)
- If using 96 kHz sample rate, try 48 kHz default

#### Disk I/O Bottleneck

**Diagnosis:**
- Session log shows queue-related warnings: `WARN Write queue full, dropped X block(s) - disk I/O bottleneck!`
- Task Manager shows high disk utilization during glitches

**Note:** The first dropped block is logged immediately to alert you of potential data loss. Subsequent drops are logged every 100 blocks to avoid log spam.

**Solution:**
- Record to SSD instead of HDD
- Avoid network shares (especially slow Wi-Fi)
- Check antivirus interfering (try disabling temporarily)

#### Audio Callback Timeout

**Diagnosis:** Very rare but possible if audio callback takes too long

**Solution:**
- Currently Hearbud offloads all I/O, but if you add processing:
  - Optimize DSP code
  - Use faster algorithms
  - Keep callback code under 5 ms per block

### Meters Not Updating or Laggy

**Symptom:** Meters stay flat or update rarely

**Causes and Solutions:**

#### Not Recording, Only Monitoring

**Diagnosis:** Status shows "Monitoring..."

**Solution:**
- Start recording to see activity; monitors also work without recording
- If no activity at all, check devices (see [Devices Not Showing](#devices-not-showing-in-dropdowns))

#### UI Thread Blocked

**Symptom:** UI freezes, meters don't update, window unresponsive

**Diagnosis:** Code running on UI thread (e.g., long-running operation not async)

**Solution (for developers):**
- Offload work to `Task.Run` or background threads
- Use `Dispatcher.Invoke` only for minimal UI updates

#### Timer Disabled

**Diagnosis:** Rare, but if `_uiTimer` stopped, meters won't refresh

**Solution (for developers):**
- Ensure timer not accidentally `Stop()`-ed
- Check timer interval: Should be 100 ms (set in MainWindow ctor)

### High Memory Usage

**Symptom:** Hearbud uses > 500 MB memory during recording

**Causes:**

#### Memory Leak

**Diagnosis:** Memory grows continuously over time

**Solutions (for developers):**
- Event handlers not unsubscribed
- Objects not disposed (IDisposable missing)
- ArrayPool buffers not returned

**Profile in Visual Studio:**
1. Debug → Windows → Diagnostic Tools
2. Use "Memory Usage" graph
3. Take snapshots before/after long recording

#### Large Sync Buffer

**Normal:** Mic sync buffer starts at 4 s × 48 kHz × 2 ch × 4 bytes ≈ 1.5 MB and caps at 30 s

**If larger:**
- Check the session log for overflow warnings (mic much faster than loopback)

#### Temporary Buffers in Callbacks

**Diagnosis:** Frequent allocations in callbacks

**Solution (for developers):**
- One `float[]` per block is by design (~200/sec); do NOT pool at the drop-oldest handoff (evictions would leak)
- The mixer itself must allocate nothing per tick (preallocated scratch)

---

## MP3 Encoding Issues

### MP3 File Not Created

**Symptom:** Stop recording, only WAV files exist, no `.mp3`

**Causes:**

#### Quality Set to "Original (WAV)"

**Diagnosis:** Output Quality dropdown shows "Original (WAV)" selected

**Solution:**
- Select a bit rate (e.g., "192 kbps (MP3)")
- `Mp3BitrateKbps` becomes > 0, enables encoding

#### MP3 Encoding Failed

**Diagnosis:** Session log shows:
```
[...] INFO Encoding MP3: ... @ 192kbps
[...] ERROR MP3 encode: NAudio.MediaFoundation.MediaFoundationException
```

**Solution:**
- See [Media Foundation Not Available](#file-corrupt-or-wont-play)

### MP3 File Smaller Than Expected / Poor Quality

**Symptom:** MP3 sounds bad, or size is tiny

**Cause:** Bit rate too low

**Solution:**
- Increase bit rate: 192 kbps minimum for music, 128 kbps for speech
- Check "Output Quality" dropdown

**Bit Rate Guide:**
- 96 kbps: Acceptable for speech, low-quality music
- 128 kbps: OK for streaming, casual listening
- 192 kbps: Standard quality, most use cases
- 256-320 kbps: High-quality, near-transparent

### MP3 Encoding Progress Stuck

**Symptom:** Progress bar freezes during "Encoding MP3..." phase

**Causes:**

#### Disk Full / No Space

**Diagnosis:** Check free space; MP3 encoding writes to disk

**Solution:** Free up space

#### Application Freeze (Not Crash)

**Diagnosis:** UI unresponsive but no error

**Possible Cause:** MP3 encoding thread deadlock (rare)

**Solution:**
- Restart Hearbud
- Check MP3 file: If partial, it's an encoding interruption, try again

---

## Crash and Log Analysis

### Application Crashes

**Symptom:** Window closes abruptly, maybe with error dialog

**Actions:**

1. **Check Crash Log:**
   - Location: `%LocalAppData%\Hearbud\logs.txt`
   - Example: `C:\Users\YourName\AppData\Local\Hearbud\logs.txt`

2. **Example Crash Log:**
   ```
   [2025-12-23 14:35:10] ERROR AppDomain.UnhandledException:
   System.NullReferenceException: Object reference not set to an instance of an object.
      at Hearbud.RecorderEngine.MixerTick()
      at Hearbud.RecorderEngine.MixerLoopAsync(CancellationToken ct)
   ```

3. **Analyze Stack Trace:**
   - Method name: Where crash occurred
   - Exception type: What kind of error
   - Common exceptions:
     - `NullReferenceException`: Uninitialized object access
     - `MMDeviceException`: Audio device disappeared
     - `IOException`: File access problem
     - `UnauthorizedAccessException`: Permission denied

### Common Crash Patterns

| Stack Trace Location | Likely Cause | Workaround |
|-----------------------|--------------|------------|
| `OnLoopData` or `OnMicData` | Audio callback exception | Check device; callbacks must never throw |
| `MixerTick` / `ProcessAndWrite` | DSP or disk I/O failure | Try different output folder; check `HealthReport` drops |
| `MainWindow` ctor | UI initialization | Check .NET version, corrupted install |
| `App.xaml.cs` | Global handler | Usually indicates deeper problem, inspect logs |

### Session Log Analysis

Session log (`*.txt` in output folder) is invaluable for diagnosing:

#### Mic Sync Issues

```text
[...] INFO AfterBlock: Mic backlog: 0.1523 s (max 0.2000 s)
```

- Steady ~0.12 s: PLL locked, healthy
- Consistently low backlog (<0.05 s): Mic slow
- Growing backlog: Mic fast (overruns) or loopback stalled
- Frequent underruns: Clock mismatch or device issue

#### Mixer / Disk Issues

```text
[...] WARN MixerTick: Disk write error: The device is not ready
```

- Disk failure, network drive offline, etc.

#### Encoding Errors

```text
[...] ERROR MP3 encode: NAudio.MediaFoundation.MediaFoundationException: HRESULT 0xC00D36E4
```

- Code indicates specific Media Foundation failure
- Check Microsoft HRESULT tables online

---

## Developer-Specific Issues

### Building Fails with NAudio Errors

**Symptom:** Compilation errors like:
```
The type or namespace name 'WasapiRecorder' could not be found
```

**Solution:**
1. Restore NuGet packages:
   ```bash
   dotnet restore
   ```
2. Check NAudio packages installed:
   - Open `Hearbud.csproj`
   - Verify `<PackageReference Include="NAudio.Wasapi" Version="3.3.0" />` and `NAudio.Core`
3. Clear NuGet cache if corrupted:
   ```bash
   dotnet nuget locals all --clear
   ```

### Debug Builds Don't Capture Audio

**Symptom:** Release mode works, Debug mode produces silent files

**Cause:** Debug build optimizations disabled, timing differences cause callback issues

**Diagnosis:**
- Set breakpoints; see if `DataAvailable` callbacks actually fire
- Check the session log for rebuild/drop warnings

**Solution:**
- Usually due to debugging interfering; try `Release` build
- Check `HealthReport.DroppedBlocks` — the mixer may be starved under a debugger

### Tests Failing Due to No Audio Hardware

**Symptom:** Integration tests fail when run on CI/headless machine

**Solution:**
- No longer needed: the whole suite is hardware-free (synthesized tones, temp
  files, reflection seams). It targets `net9.0-windows`, so run it on Windows
  or cross-check with a raw-`csc` harness (see `CONTRIBUTING.md`
  § Automated Tests).

### Profiling Performance Issues

**Tools to use:**

#### Visual Studio Profiler
1. Debug → Performance Profiler
2. Choose "CPU Usage"
3. Run app and perform recording
4. Stop profiling, analyze hot paths

#### ETW Tracing (Windows Event Tracing)
Use WPT (Windows Performance Toolkit) for GPU, disk, DPC analysis.

#### Diagnostic Tools Window
1. Debug → Windows → Diagnostic Tools
2. Monitor CPU usage, memory, GC pauses

**Common Hot Paths:**
- `MixerTick` / `ProcessMixBlock`: Most time spent here — per-sample DSP plus disk writes
- `TimestampedMicBuffer.Read`: Linear-interp pull; verify no per-tick allocs
- MP3 encoding: Post-process on `Task.Run` (masters already safe)

---

## Additional Resources

- **For Users:** Report issues on [GitHub Issues](https://github.com/radekstepan/hearbud/issues)
- **For Developers:** See [ARCHITECTURE.md](ARCHITECTURE.md) for design, [WORKING_WITH_AUDIO.md](WORKING_WITH_AUDIO.md) for audio specifics, and [CONTRIBUTING.md](CONTRIBUTING.md) for development guidelines

---

## Quick Reference: Error Messages and Meanings

| Error Message | Likely Cause | Quick Fix |
|---------------|--------------|-----------|
| `Object disposed` | Engine used after `Dispose` | Check disposal order |
| `Device not found` | Device ID invalid/disappeared | Refresh devices, select again |
| `Audio endpoint cannot be used` | Exclusive mode in other app | Disable exclusive mode in Windows |
| `Access denied` | Permission to write folder | Change output folder |
| `MediaFoundationException` | MP3 encoder missing | Install Media Feature Pack (Win N) |
| `NullReference` in callbacks | Uninitialized audio source | Ensure `Start()` called before capture |
| `Disk full` | No space for files | Free disk space |
| `IOException: path too long` | Output path > 260 chars | Use shorter folder names |

---

## Still Stuck?

1. **Search existing issues** on GitHub before posting
2. **Gather info:**
   - Hearbud version
   - Windows version (10/11, build number)
   - Audio hardware (mic, speakers, drivers)
   - Steps to reproduce
   - Log files (`logs.txt`, session `*.txt`)
3. **Post issue** with clear title and reproduction steps

Thanks for using Hearbud! 🎧
