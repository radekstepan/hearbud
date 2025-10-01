// ----------------------------------------------------------------------------------------------------
// PURPOSE
// ----------------------------------------------------------------------------------------------------
// This class records Windows system audio (WASAPI loopback) and, optionally, a microphone, in real-time.
// It writes three WAV files during capture:
//   1) <base>-system.wav  - the raw system/loopback stream
//   2) <base>-mic.wav     - the raw microphone stream (converted to match loopback format)
//   3) <base>-mix.wav     - a simple averaged mix of system + mic (with user-controlled gains)
// After stopping, it encodes the mix.wav to MP3 (<base>.mp3) using Media Foundation.
//
// DESIGN INTENT
// - Loopback (system) is the "clock source": whenever loopback provides a chunk, we pull a same-sized
//   chunk from a microphone ring buffer and mix them. This preserves relative timing and prevents
//   'line up at start' errors. If mic is late, we zero-fill the gap (so silence is kept).
// - If loopback is silent (e.g., user is speaking but no system audio), we still want files produced.
//   We track time since last loopback block and, if it exceeds a threshold, we *drive* the output using
//   the mic stream alone (system gets zeros, mix = scaled mic). This ensures mic-only sessions still work.
// - We always write raw per-source WAVs while recording (no post-session rendering needed for those).
//   The MP3 is encoded at stop time from the already-written mix.wav. This decouples real-time capture
//   from potentially slower MP3 encoding.
// - Gains (MicGain / LoopGain) are applied in metering and mixing. The mix uses a simple 0.5*(s+m) average
//   to maintain ~6 dB headroom and reduce clipping. We apply a soft clipper before final clamp.
// - Sample-rate / channel conversion for mic -> loopback format occurs on the fly using a simple linear
//   resampler and basic channel up/down-mix. Loopback format defines the output WaveFormat for all files.
// - The mic ring buffer absorbs clock & scheduling jitter between the two capture callbacks so that the
//   data aligned to loopback frames is consistent when mixing.
// - Logging is best-effort to a text file next to outputs.
//
// THREADING MODEL
// - CSCore raises DataAvailable events on its own threads for each capture.
// - We keep work short in handlers: read/convert buffers, push/read ring, write WAV.
// - Shared state in the mic ring uses a lock. Other fields are mostly written in Start/Stop paths.
//
// FAILURE & STATUS REPORTING
// - We surface status via the Status event: Info/Error/Stopped and paths of the produced files.
// - Stop() reports success if *any* of the files are non-empty, so the UI can always show a "Saved" toast.
// - Errors during MP3 encoding do not invalidate the WAVs; we log and proceed.
//
// PERFORMANCE NOTES
// - Buffers grow to next power-of-two and are reused (Array.Resize used sparingly).
// - We avoid allocations in hot paths except when a larger buffer becomes necessary.
// - FillWithZeros=true on SoundInSource ensures continuous streams (silence when device has no new frames).
//
// LIMITATIONS
// - Resampler is linear (good enough for voice/meeting scenarios, not audiophile grade).
// - Mixing is a simple average; final stage uses soft-clip + clamp to [-1,1].
// - We rely on loopback sample rate/channels. If device settings change mid-session, behavior is undefined.
//
// ----------------------------------------------------------------------------------------------------

using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading; // ThreadLocal<Random> for dither RNG
using CSCore;
using CSCore.Codecs.WAV;
using CSCore.CoreAudioAPI;
using CSCore.MediaFoundation; // MP3 encoder
using CSCore.SoundIn;
using CSCore.Streams;

namespace WasapiLoopMix
{
    // Identifies which source a level meter event refers to.
    public enum LevelSource { Mic, System }

    // Event payload for real-time level updates to the UI.
    // Added RMS: represents "loudness" over the throttling window; Peak indicates "headroom".
    public sealed class LevelChangedEventArgs : EventArgs
    {
        public LevelChangedEventArgs(LevelSource source, float rms, float peak, bool clipped)
        { Source = source; Rms = rms; Peak = peak; Clipped = clipped; }
        public LevelSource Source { get; }
        public float Rms { get; }     // Root-mean-square over the most recent window (post-gain, pre-clip)
        public float Peak { get; }    // Maximum absolute sample over the same window (post-gain)
        public bool Clipped { get; }  // True if any sample exceeded |1.0| in the window (before soft clip)
    }

    // High-level status for UI (info/errors/stopped summary).
    public enum EngineStatusKind { Info, Error, Stopped }

    // Detailed status payload; used to show paths and success in UI toasts.
    public sealed class EngineStatusEventArgs : EventArgs
    {
        public EngineStatusEventArgs(
            EngineStatusKind kind, string message, bool success = false,
            string? outputPathSystem = null, string? outputPathMic = null,
            string? outputPathMix = null, string? outputPathMp3 = null)
        {
            Kind = kind; Message = message; Success = success;
            OutputPathSystem = outputPathSystem ?? "";
            OutputPathMic    = outputPathMic    ?? "";
            OutputPathMix    = outputPathMix    ?? "";
            OutputPathMp3    = outputPathMp3    ?? "";
        }
        public EngineStatusKind Kind { get; }
        public string Message { get; }
        public bool Success { get; }
        public string OutputPathSystem { get; }
        public string OutputPathMic { get; }
        public string OutputPathMix { get; }
        public string OutputPathMp3 { get; }
    }

    // Startup options supplied by the UI layer.
    public sealed class RecorderStartOptions
    {
        public string OutputPath { get; set; } = ""; // base (no extension)
        public string LoopbackDeviceId { get; set; } = "";
        public string? MicDeviceId { get; set; }
        public bool IncludeMic { get; set; } = true; // mic always included
        public int Mp3BitrateKbps { get; set; } = 192; // mix->MP3 bitrate
    }

    public sealed class RecorderEngine : IDisposable
    {
        // These get set to the loopback device format on open; we standardize everything to it.
        private int _outRate = 48000;
        private int _outChannels = 2;

        // Size hint used in buffers; handlers can read variable sizes from CSCore; we grow buffers as needed.
        private const int BlockFrames = 1024;

        // UI-controlled gains; affect meters + final mix balance.
        public double MicGain { get; set; } = 1.0;   // meters + mix balance
        public double LoopGain { get; set; } = 1.0;  // meters + mix balance

        // UI event hooks
        public event EventHandler<LevelChangedEventArgs>? LevelChanged;
        public event EventHandler<EngineStatusEventArgs>? Status;

        // CSCore capture objects for system loopback and mic.
        private WasapiLoopbackCapture? _loopCap;
        private WasapiCapture? _micCap;

        // Wrappers exposing DataAvailable and sample conversion.
        private SoundInSource? _loopIn;
        private SoundInSource? _micIn;

        // Float sample sources (post CSCore conversion to float).
        private ISampleSource? _loopSrc;
        private ISampleSource? _micSrc;

        // Whether capture devices are started (callbacks firing).
        private bool _monitoring;
        // Whether we're writing WAVs (recording state).
        private bool _recording;

        // Per-output writers/paths
        private WaveWriter? _wavSys;
        private WaveWriter? _wavMic;

        // We support a higher bit-depth mix WAV to preserve quality before MP3 encoding.
        private WaveWriter? _wavMix;
        private readonly bool _mixUse32Bit = true; // set true to write 32-bit PCM mix.wav

        private string _pathSys = "";
        private string _pathMic = "";
        private string _pathMix = "";
        private string _pathMp3 = "";
        private int _kbps = 192;

        // Logging to a text file next to outputs (best effort).
        private string _logPath = "";
        private StreamWriter? _log;

        // Scratch audio buffers reused across callbacks (avoid per-block allocs).
        private float[] _loopBufF     = new float[BlockFrames * 8];
        private float[] _micInBuf     = new float[BlockFrames * 8];
        private float[] _micConvBuf   = new float[BlockFrames * 8];
        private float[] _tmpMicBlock  = new float[BlockFrames * 8];
        private float[] _mixBufF      = new float[BlockFrames * 8];

        // PCM staging for WaveWriter (bytes).
        private byte[]  _pcm16Sys     = new byte[BlockFrames * 8 * 2];
        private byte[]  _pcm16Mic     = new byte[BlockFrames * 8 * 2];

        // Mix can target 16 or 32-bit. We keep both buffers and use the one we need.
        private byte[]  _pcm16Mix     = new byte[BlockFrames * 8 * 2];
        private byte[]  _pcm32Mix     = new byte[BlockFrames * 8 * 4];

        // Microphone ring buffer:
        // - Stores float samples already converted to loopback's format.
        // - On each loopback chunk we read the same number of samples from here
        //   (or zero-fill if underrun) to keep alignment with loopback timing.
        private readonly object _micRingLock = new();
        private float[] _micRing = new float[48000 * 4]; // ~2s stereo at 48kHz
        private int _micR, _micW, _micCount;

        // Loopback activity tracking:
        // - If loopback is silent for > LoopSilentMsThreshold, we let mic drive the output, so that
        //   users can still record mic-only sessions without system audio.
        private static readonly double _tickMs = 1000.0 / Stopwatch.Frequency;
        private long _lastLoopTick = 0;
        private const int LoopSilentMsThreshold = 200; // if no system frames for >200ms, drive mix from mic

        // ===== Diagnostics =====
        private string _loopDevName = "";
        private string _micDevName  = "(none)";
        private long _micUnderrunBlocks = 0;
        private long _loopBlockCounter = 0;
        private double _lastMicBacklogSec = 0.0;
        private double _micBacklogSecMax = 0.0;
        private const int BacklogLogEveryNBlocks = 50;

        // ===== Level throttling (smooth, low-CPU UI meters, ~20 Hz per source) =====
        private const double LevelThrottleMs = 50.0; // ~20 Hz
        private long _lastLevelTickMic = 0;
        private long _lastLevelTickSys = 0;
        private float _peakSinceLastMic = 0f;
        private float _peakSinceLastSys = 0f;
        private double _rmsSumSinceLastMic = 0.0;
        private double _rmsSumSinceLastSys = 0.0;
        private long _rmsCountSinceLastMic = 0;
        private long _rmsCountSinceLastSys = 0;
        private bool _clippedSinceLastMic = false;
        private bool _clippedSinceLastSys = false;

        private volatile bool _disposed;

        // ===== Public API =====

        // Start device capture streams (no file writing). Used for live meters / "armed" state.
        public void Monitor(RecorderStartOptions opts)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(RecorderEngine));
            StopInternal(fullStop: true);
            OpenDevices(opts);

            if (!_monitoring)
            {
                _loopIn!.DataAvailable += OnLoopbackData;
                if (_micIn != null) _micIn.DataAvailable += OnMicData;

                _loopCap!.Start();
                _micCap?.Start();

                _monitoring = true;
                _lastLoopTick = Stopwatch.GetTimestamp();
                _lastLevelTickMic = _lastLevelTickSys = _lastLoopTick; // initialize throttling timers
                _peakSinceLastMic = _peakSinceLastSys = 0f;
                _rmsSumSinceLastMic = _rmsSumSinceLastSys = 0.0;
                _rmsCountSinceLastMic = _rmsCountSinceLastSys = 0;
                _clippedSinceLastMic = _clippedSinceLastSys = false;

                RaiseStatus(EngineStatusKind.Info, "Monitoring…");
                Info("Monitoring started");
            }
        }

        // Start recording (creates per-source WAVs + mixed WAV).
        public void Start(RecorderStartOptions opts)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(RecorderEngine));
            Monitor(opts); // opens devices & starts callbacks

            _kbps = Math.Clamp(opts.Mp3BitrateKbps, 64, 320);

            var basePath = string.IsNullOrWhiteSpace(opts.OutputPath)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "Recordings", $"rec-{DateTime.Now:yyyyMMdd_HHmmss}")
                : opts.OutputPath;

            var outDir = Path.GetDirectoryName(basePath)!;
            var baseName = Path.GetFileName(basePath);
            Directory.CreateDirectory(outDir);

            // Non-clobbering filenames for all outputs & log.
            _pathSys = UniquePath(Path.Combine(outDir, $"{baseName}-system.wav"));
            _pathMic = UniquePath(Path.Combine(outDir, $"{baseName}-mic.wav"));
            _pathMix = UniquePath(Path.Combine(outDir, $"{baseName}-mix.wav"));
            _pathMp3 = UniquePath(Path.Combine(outDir, $"{baseName}.mp3"));

            _logPath = UniquePath(Path.Combine(outDir, $"{baseName}.txt"));
            OpenLog();

            Info("===== Session start =====");
            Info($"Gains: LoopGain={LoopGain:F3}, MicGain={MicGain:F3}");
            Info($"Devices: Loopback='{_loopDevName}', Mic='{_micDevName}'");
            Info($"System WAV: {_pathSys}");
            Info($"Mic    WAV: {_pathMic}");
            Info($"Mix    WAV: {_pathMix} ({(_mixUse32Bit ? "32-bit PCM" : "16-bit PCM")})");
            Info($"Mix   MP3 : {_pathMp3} @ {_kbps}kbps");
            Info($"Loopback fmt: sr={_outRate}, ch={_outChannels}");

            _wavSys = new WaveWriter(_pathSys, new CSCore.WaveFormat(_outRate, 16, _outChannels));
            _wavMic = new WaveWriter(_pathMic, new CSCore.WaveFormat(_outRate, 16, _outChannels));
            // Use a higher bit depth for the MIX to avoid an extra quantization step before MP3.
            _wavMix = new WaveWriter(_pathMix, new CSCore.WaveFormat(_outRate, _mixUse32Bit ? 32 : 16, _outChannels));

            lock (_micRingLock) { _micR = _micW = _micCount = 0; }

            _micUnderrunBlocks = 0;
            _loopBlockCounter = 0;
            _lastMicBacklogSec = 0.0;
            _micBacklogSecMax = 0.0;

            _recording = true;
            RaiseStatus(EngineStatusKind.Info, "Recording to WAV…");
            Info("Recording started");
        }

        // Stop recording and monitoring; encode MP3 from mix.wav.
        // IMPORTANT: fully release ALL file handles (WAV, MP3, LOG) so the user can delete/move files
        // while the app stays open and ready for another session.
        public void Stop()
        {
            if (_disposed) return;

            // Close WAV writers first (finalizes headers).
            bool okSys = false, okMic = false, okMix = false, okMp3 = false;
            try { _wavSys?.Dispose(); okSys = File.Exists(_pathSys) && new FileInfo(_pathSys).Length > 0; } catch { }
            try { _wavMic?.Dispose(); okMic = File.Exists(_pathMic) && new FileInfo(_pathMic).Length > 0; } catch { }
            try { _wavMix?.Dispose(); okMix = File.Exists(_pathMix) && new FileInfo(_pathMix).Length > 0; } catch { }
            _wavSys = null; _wavMic = null; _wavMix = null;

            _recording = false;

            // Encode MP3 from the mix WAV (even if it’s mic-only mix).
            Exception? encEx = null;
            try
            {
                if (okMix && File.Exists(_pathMix))
                {
                    Info($"Encoding MP3: {_pathMix} → {_pathMp3} @ {_kbps}kbps");
                    using var reader = new WaveFileReader(_pathMix);
                    using var encoder = MediaFoundationEncoder.CreateMP3Encoder(reader.WaveFormat, _pathMp3, _kbps * 1000);
                    byte[] buf = new byte[1 << 16];
                    int read;
                    while ((read = reader.Read(buf, 0, buf.Length)) > 0)
                        encoder.Write(buf, 0, read);
                    okMp3 = File.Exists(_pathMp3) && new FileInfo(_pathMp3).Length > 0;
                    Info($"MP3 encode ok={okMp3}");
                }
                else
                {
                    Info("Mix WAV missing or empty; skipping MP3 encode.");
                }
            }
            catch (Exception ex)
            {
                encEx = ex;
                Error("MP3 encode", ex);
            }

            // Stop devices & unsubscribe callbacks (releases capture-related handles).
            StopInternal(fullStop: false);

            // Final diagnostics to the log before closing it.
            Info($"Mic ring underruns (micRead < loop): {_micUnderrunBlocks} block(s). " +
                 $"Peak mic backlog: {_micBacklogSecMax:F4}s, Last backlog: {_lastMicBacklogSec:F4}s");
            Info("Recording stopped");
            Info("===== Session end =====");

            // Close the session log so the .txt file is not locked after Stop.
            TryDispose(ref _log);
            _logPath = ""; // mark as closed; next Start() will set & open a new log

            // Consider success if ANY file has content (so UI always sees a saved toast)
            bool anyOk = okSys || okMic || okMix || okMp3;
            RaiseStatus(
                EngineStatusKind.Stopped,
                anyOk ? "Saved file(s)" : $"Recording stopped (files may be empty){(encEx != null ? " – MP3 encode failed" : "")}",
                success: anyOk,
                outputPathSystem: _pathSys, outputPathMic: _pathMic, outputPathMix: _pathMix, outputPathMp3: _pathMp3);

            // Reset paths so subsequent sessions don't accidentally reference previous files.
            _pathSys = _pathMic = _pathMix = _pathMp3 = "";
        }

        public void StopMonitor() => StopInternal(fullStop: true);

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try { _wavSys?.Dispose(); } catch { }
            try { _wavMic?.Dispose(); } catch { }
            try { _wavMix?.Dispose(); } catch { }
            _wavSys = null; _wavMic = null; _wavMix = null;

            StopInternal(fullStop: true);
            TryDispose(ref _log);
        }

        // ===== Devices =====

        private void OpenDevices(RecorderStartOptions opts)
        {
            bool needReopen =
                _loopCap == null ||
                _loopCap.Device.DeviceID != opts.LoopbackDeviceId ||
                ((_micCap == null) != !string.IsNullOrWhiteSpace(opts.MicDeviceId)) ||
                (_micCap != null && _micCap.Device.DeviceID != (opts.MicDeviceId ?? ""));

            if (!needReopen) return;

            Info("Opening devices…");

            try { if (_loopIn != null) _loopIn.DataAvailable -= OnLoopbackData; } catch { }
            try { if (_micIn  != null) _micIn.DataAvailable  -= OnMicData; }      catch { }
            TryStopDispose(ref _loopCap);
            TryStopDispose(ref _micCap);
            TryDispose(ref _loopIn);
            TryDispose(ref _micIn);
            TryDispose(ref _loopSrc);
            TryDispose(ref _micSrc);

            MMDevice loopDev;
            MMDevice? micDev = null;
            using (var mmde = new MMDeviceEnumerator())
            {
                loopDev = mmde.GetDevice(opts.LoopbackDeviceId);
                if (!string.IsNullOrWhiteSpace(opts.MicDeviceId))
                    micDev = mmde.GetDevice(opts.MicDeviceId!);
            }

            _loopDevName = loopDev?.FriendlyName ?? "";
            _micDevName  = micDev?.FriendlyName ?? "(none)";

            Info($"Loopback: {loopDev?.FriendlyName}");
            if (micDev != null) Info($"Mic: {micDev.FriendlyName}"); else Info("Mic: (none)");

            _loopCap = new WasapiLoopbackCapture { Device = loopDev };
            _loopCap.Initialize();
            _outRate     = _loopCap.WaveFormat.SampleRate;
            _outChannels = _loopCap.WaveFormat.Channels;

            _loopIn  = new SoundInSource(_loopCap) { FillWithZeros = true };
            _loopSrc = _loopIn.ToSampleSource();

            if (micDev != null)
            {
                _micCap = new WasapiCapture { Device = micDev };
                _micCap.Initialize();

                _micIn  = new SoundInSource(_micCap) { FillWithZeros = true };
                _micSrc = _micIn.ToSampleSource();

                lock (_micRingLock) { _micR = _micW = _micCount = 0; }
            }
            else
            {
                RaiseStatus(EngineStatusKind.Error, "Microphone device not found/selected.");
            }

            try
            {
                using var ac = AudioClient.FromMMDevice(loopDev);
                var mix = ac.MixFormat;
                Info($"Loopback Device MixFormat: sr={mix.SampleRate}, ch={mix.Channels}, bits={mix.BitsPerSample}, tag={mix.WaveFormatTag}");
            } catch { }
            Info($"Loopback Capture WaveFormat: sr={_loopCap.WaveFormat.SampleRate}, ch={_loopCap.WaveFormat.Channels}, bits={_loopCap.WaveFormat.BitsPerSample}, tag={_loopCap.WaveFormat.WaveFormatTag}");

            if (micDev != null)
            {
                try
                {
                    using var acm = AudioClient.FromMMDevice(micDev);
                    var mixm = acm.MixFormat;
                    Info($"Mic Device MixFormat: sr={mixm.SampleRate}, ch={mixm.Channels}, bits={mixm.BitsPerSample}, tag={mixm.WaveFormatTag}");
                } catch { }
                Info($"Mic Capture WaveFormat: sr={_micCap!.WaveFormat.SampleRate}, ch={_micCap.WaveFormat.Channels}, bits={_micCap.WaveFormat.BitsPerSample}, tag={_micCap.WaveFormat.WaveFormatTag}");
            }
        }

        private void StopInternal(bool fullStop)
        {
            if (_monitoring)
            {
                try { _loopIn!.DataAvailable -= OnLoopbackData; } catch { }
                try { _micIn! .DataAvailable -= OnMicData; }      catch { }
                TryStopDispose(ref _loopCap);
                TryStopDispose(ref _micCap);
                TryDispose(ref _loopIn);
                TryDispose(ref _micIn);
                TryDispose(ref _loopSrc);
                TryDispose(ref _micSrc);
                _monitoring = false;
            }

            if (fullStop) Info("Stopped.");
        }

        // ===== DataAvailable =====

        private void OnMicData(object? sender, DataAvailableEventArgs e)
        {
            if (_micSrc == null || _micIn == null || _micCap == null) return;
            try
            {
                int blockAlignBytes = _micIn.WaveFormat.BlockAlign; // bytes per frame
                if (blockAlignBytes <= 0) return;

                int framesAvail = e.ByteCount / blockAlignBytes;
                if (framesAvail <= 0) return;

                int floatSamplesToRead = framesAvail * _micSrc.WaveFormat.Channels;
                EnsureCapacity(ref _micInBuf, floatSamplesToRead);

                int got = ReadExactSamples(_micSrc, _micInBuf, floatSamplesToRead);
                if (got <= 0) return;

                int conv = ConvertToTarget(_micInBuf, got, _micSrc.WaveFormat, _outRate, _outChannels, ref _micConvBuf);

                // Determine whether loopback has been silent long enough to enter mic-driven mode.
                long nowTicksA = Stopwatch.GetTimestamp();
                double msSinceLoop = (nowTicksA - _lastLoopTick) * _tickMs;
                bool loopSilent = msSinceLoop > LoopSilentMsThreshold;

                // meters (mic): compute peak and accumulate RMS cheaply over this block
                float blockPeak = 0f;
                double blockRmsSum = 0.0;
                bool blockClipped = false;
                for (int i = 0; i < conv; i++)
                {
                    float sample = (float)(_micConvBuf[i] * MicGain);
                    float abs = MathF.Abs(sample);
                    if (abs > blockPeak) blockPeak = abs;
                    blockRmsSum += (double)abs * abs;
                    if (abs > 1f) blockClipped = true;
                }
                if (blockPeak > _peakSinceLastMic) _peakSinceLastMic = blockPeak;
                _rmsSumSinceLastMic += blockRmsSum;
                _rmsCountSinceLastMic += conv;
                _clippedSinceLastMic |= blockClipped;

                // Throttled level event (~20 Hz)
                double msSince = (nowTicksA - _lastLevelTickMic) * _tickMs;
                if (msSince >= LevelThrottleMs && _rmsCountSinceLastMic > 0)
                {
                    float rms = (float)Math.Sqrt(_rmsSumSinceLastMic / _rmsCountSinceLastMic);
                    LevelChanged?.Invoke(this, new LevelChangedEventArgs(LevelSource.Mic, rms, _peakSinceLastMic, _clippedSinceLastMic));
                    _peakSinceLastMic = 0f;
                    _rmsSumSinceLastMic = 0.0;
                    _rmsCountSinceLastMic = 0;
                    _clippedSinceLastMic = false;
                    _lastLevelTickMic = nowTicksA;
                }

                // RING-BUFFER POLICY FIX:
                // When loopback is silent and we are *already writing* mic-only audio to the mix/system files,
                // we must NOT accumulate those mic samples in the ring; otherwise they will be mixed again with
                // *future* loopback when it resumes, causing "old mic over new system" echoes.
                if (loopSilent)
                {
                    // Drop any existing backlog to realign the timeline to "now".
                    lock (_micRingLock)
                    {
                        _micR = _micW;
                        _micCount = 0;
                    }
                }
                else
                {
                    // Normal path: push into ring for the loopback-driven mixer to pull from.
                    lock (_micRingLock)
                    {
                        EnsureRingCapacity(conv);
                        for (int i = 0; i < conv; i++)
                        {
                            _micRing[_micW] = _micConvBuf[i];
                            _micW = (_micW + 1) % _micRing.Length;
                            if (_micCount < _micRing.Length) _micCount++;
                            else _micR = (_micR + 1) % _micRing.Length;
                        }
                    }
                }

                // Always write mic WAV (16-bit PCM with TPDF dither).
                if (_recording && _wavMic != null)
                {
                    EnsureCapacity(ref _pcm16Mic, conv * 2);
                    FloatToPcm16(_micConvBuf, _pcm16Mic, conv); // includes TPDF dither
                    _wavMic.Write(_pcm16Mic, 0, conv * 2);
                }

                // Mic-driven path when loopback is silent:
                // - Write zeros to system.wav and mic-only (gain+headroom+soft-clip) to mix.wav.
                if (_recording && _wavMix != null && _wavSys != null && loopSilent)
                {
                    // system zeros
                    EnsureCapacity(ref _pcm16Sys, conv * 2);
                    Array.Clear(_pcm16Sys, 0, conv * 2);
                    _wavSys.Write(_pcm16Sys, 0, conv * 2);

                    // mic-only mix (apply gain, 0.5 headroom, soft-clip)
                    EnsureCapacity(ref _mixBufF, conv);
                    for (int i = 0; i < conv; i++)
                    {
                        float m = (float)(_micConvBuf[i] * MicGain);
                        float a = m * 0.5f;
                        a = SoftClipIfNeeded(a);
                        _mixBufF[i] = a;
                    }

                    if (_mixUse32Bit)
                    {
                        EnsureCapacity(ref _pcm32Mix, conv * 4);
                        FloatToPcm32(_mixBufF, _pcm32Mix, conv);
                        _wavMix.Write(_pcm32Mix, 0, conv * 4);
                    }
                    else
                    {
                        EnsureCapacity(ref _pcm16Mix, conv * 2);
                        FloatToPcm16(_mixBufF, _pcm16Mix, conv); // includes TPDF dither
                        _wavMix.Write(_pcm16Mix, 0, conv * 2);
                    }
                }
            }
            catch (Exception ex) { Error("OnMicData", ex); }
        }

        private void OnLoopbackData(object? sender, DataAvailableEventArgs e)
        {
            if (_loopSrc == null || _loopCap == null || _loopIn == null) return;

            try
            {
                _lastLoopTick = Stopwatch.GetTimestamp();

                int bytesPerFrame = _loopIn.WaveFormat.BlockAlign; // bytes per frame
                if (bytesPerFrame <= 0) return;

                int frames = e.ByteCount / bytesPerFrame;
                if (frames <= 0) return;

                int wantFloats = frames * _outChannels;
                EnsureCapacity(ref _loopBufF, wantFloats);

                int gotLoop = ReadExactSamples(_loopSrc, _loopBufF, wantFloats);
                if (gotLoop <= 0) return;

                // meters (system): compute peak and accumulate RMS over this block
                float blockPeak = 0f;
                double blockRmsSum = 0.0;
                bool blockClipped = false;
                for (int i = 0; i < gotLoop; i++)
                {
                    float sample = (float)(_loopBufF[i] * LoopGain);
                    float abs = MathF.Abs(sample);
                    if (abs > blockPeak) blockPeak = abs;
                    blockRmsSum += (double)abs * abs;
                    if (abs > 1f) blockClipped = true;
                }
                if (blockPeak > _peakSinceLastSys) _peakSinceLastSys = blockPeak;
                _rmsSumSinceLastSys += blockRmsSum;
                _rmsCountSinceLastSys += gotLoop;
                _clippedSinceLastSys |= blockClipped;

                long nowTicks = _lastLoopTick;
                double msSince = (nowTicks - _lastLevelTickSys) * _tickMs;
                if (msSince >= LevelThrottleMs && _rmsCountSinceLastSys > 0)
                {
                    float rms = (float)Math.Sqrt(_rmsSumSinceLastSys / _rmsCountSinceLastSys);
                    LevelChanged?.Invoke(this, new LevelChangedEventArgs(LevelSource.System, rms, _peakSinceLastSys, _clippedSinceLastSys));
                    _peakSinceLastSys = 0f;
                    _rmsSumSinceLastSys = 0.0;
                    _rmsCountSinceLastSys = 0;
                    _clippedSinceLastSys = false;
                    _lastLevelTickSys = nowTicks;
                }

                // Write raw system WAV (16-bit + TPDF dither)
                if (_recording && _wavSys != null)
                {
                    EnsureCapacity(ref _pcm16Sys, gotLoop * 2);
                    FloatToPcm16(_loopBufF, _pcm16Sys, gotLoop); // includes TPDF dither
                    _wavSys.Write(_pcm16Sys, 0, gotLoop * 2);
                }

                // Backlog snapshot for drift diagnostics
                int snapshotMicCount;
                lock (_micRingLock)
                {
                    snapshotMicCount = _micCount;
                }
                double backlogSec = (double)snapshotMicCount / (_outChannels * _outRate);
                _lastMicBacklogSec = backlogSec;
                if (backlogSec > _micBacklogSecMax) _micBacklogSecMax = backlogSec;

                // Pull same-length mic block from ring (zero-fill if underrun)
                EnsureCapacity(ref _tmpMicBlock, gotLoop);
                int micRead;
                lock (_micRingLock)
                {
                    micRead = Math.Min(gotLoop, _micCount);
                    for (int i = 0; i < micRead; i++)
                    {
                        _tmpMicBlock[i] = _micRing[_micR];
                        _micR = (_micR + 1) % _micRing.Length;
                    }
                    _micCount -= micRead;
                }
                if (micRead < gotLoop)
                {
                    Array.Clear(_tmpMicBlock, micRead, gotLoop - micRead);
                    _micUnderrunBlocks++;
                }

                _loopBlockCounter++;
                if (_loopBlockCounter % BacklogLogEveryNBlocks == 0)
                {
                    Info($"Mic ring backlog: {backlogSec:F4} s (max {_micBacklogSecMax:F4} s)");
                }

                // Mix: average with gains, then soft-clip the result *before* final clamp/quantize.
                EnsureCapacity(ref _mixBufF, gotLoop);
                for (int i = 0; i < gotLoop; i++)
                {
                    float s = (float)(_loopBufF[i] * LoopGain);
                    float m = (float)(_tmpMicBlock[i] * MicGain);
                    float a = (s + m) * 0.5f; // -6 dB headroom
                    a = SoftClipIfNeeded(a);
                    _mixBufF[i] = a;
                }

                if (_recording && _wavMix != null)
                {
                    if (_mixUse32Bit)
                    {
                        EnsureCapacity(ref _pcm32Mix, gotLoop * 4);
                        FloatToPcm32(_mixBufF, _pcm32Mix, gotLoop);
                        _wavMix.Write(_pcm32Mix, 0, gotLoop * 4);
                    }
                    else
                    {
                        EnsureCapacity(ref _pcm16Mix, gotLoop * 2);
                        FloatToPcm16(_mixBufF, _pcm16Mix, gotLoop); // includes TPDF dither
                        _wavMix.Write(_pcm16Mix, 0, gotLoop * 2);
                    }
                }
            }
            catch (Exception ex) { Error("OnLoopbackData", ex); }
        }

        // Read exactly N float samples from an ISampleSource
        private static int ReadExactSamples(ISampleSource src, float[] dst, int count)
        {
            int total = 0;
            while (total < count)
            {
                int got = src.Read(dst, total, count - total);
                if (got <= 0) break;
                total += got;
            }
            return total;
        }

        // ===== Conversion & sample utils =====

        private static int ConvertToTarget(float[] src, int floatCount, CSCore.WaveFormat srcFmt,
                                           int dstRate, int dstCh, ref float[] dst)
        {
            int srcCh = srcFmt.Channels;
            int srcRate = srcFmt.SampleRate;

            int srcFrames = floatCount / srcCh;
            if (srcFrames <= 0) return 0;

            int dstFrames = (srcRate == dstRate) ? srcFrames
                          : (int)((long)srcFrames * dstRate / srcRate);

            EnsureCapacity(ref dst, Math.Max(dstCh, srcCh) * dstFrames);

            // Resample (linear), then channel convert
            float[] temp = src;
            int tempFrames = srcFrames;
            if (srcRate != dstRate)
            {
                temp = new float[srcCh * dstFrames];
                float ratio = (float)srcRate / dstRate;
                int di = 0;
                for (int f = 0; f < dstFrames; f++)
                {
                    float sp = f * ratio;
                    int i0 = (int)sp;
                    int i1 = Math.Min(i0 + 1, srcFrames - 1);
                    float t = sp - i0;

                    for (int c = 0; c < srcCh; c++)
                    {
                        float s0 = src[i0 * srcCh + c];
                        float s1 = src[i1 * srcCh + c];
                        temp[di++] = s0 + (s1 - s0) * t;
                    }
                }
                tempFrames = dstFrames;
            }

            int di2 = 0;
            if (srcCh == dstCh)
            {
                Array.Copy(temp, 0, dst, 0, tempFrames * srcCh);
                di2 = tempFrames * srcCh;
            }
            else if (srcCh == 1 && dstCh == 2)
            {
                for (int f = 0; f < tempFrames; f++)
                {
                    float m = temp[f];
                    dst[di2++] = m; dst[di2++] = m;
                }
            }
            else if (srcCh >= 2 && dstCh == 1)
            {
                for (int f = 0; f < tempFrames; f++)
                {
                    float l = temp[f * srcCh + 0];
                    float r = temp[f * srcCh + 1];
                    dst[di2++] = 0.5f * (l + r);
                }
            }
            else
            {
                for (int f = 0; f < tempFrames; f++)
                {
                    int si = f * srcCh;
                    for (int c = 0; c < dstCh; c++)
                        dst[di2++] = temp[si + Math.Min(c, srcCh - 1)];
                }
            }

            return di2;
        }

        // Soft clipper: only engages when |x| > 1.0; uses tanh for a gentle knee then clamps to [-1,1].
        private static float SoftClipIfNeeded(float x)
        {
            if (x > 1f || x < -1f) x = MathF.Tanh(x);
            if (x > 1f) x = 1f; else if (x < -1f) x = -1f;
            return x;
        }

        // ====== DITHERED 16-BIT QUANTIZATION ======
        // TPDF (Triangular) dither: add (U1 - U2) * 1 LSB prior to rounding.
        // Implementation detail:
        //  - We operate in the integer domain right before rounding (scale by 32767).
        //  - (rand1 - rand2) has triangular pdf in (-1, +1), i.e. ±1 LSB peak-to-peak.
        //  - Cheap and thread-safe via ThreadLocal<Random>; avoids crunchy artefacts at very low levels.
        private static readonly ThreadLocal<Random> _rng =
            new ThreadLocal<Random>(() => new Random(unchecked(Environment.TickCount * 31 + Thread.CurrentThread.ManagedThreadId)));

        private static void FloatToPcm16(float[] src, byte[] dst, int count)
        {
            int j = 0;
            var rng = _rng.Value!;
            for (int i = 0; i < count; i++)
            {
                float v = src[i];
                // Safety clamp to avoid NaNs or out-of-range
                if (v > 1f) v = 1f; else if (v < -1f) v = -1f;

                // Scale to integer domain
                float scaled = v * 32767.0f;

                // TPDF dither: two independent uniforms in [0,1), difference in (-1, +1)
                float dither = (float)rng.NextDouble() - (float)rng.NextDouble();
                float withDither = scaled + dither; // ±1 LSB peak-to-peak

                int s = (int)MathF.Round(withDither);
                if (s > short.MaxValue) s = short.MaxValue;
                else if (s < short.MinValue) s = short.MinValue;

                dst[j++] = (byte)(s & 0xFF);
                dst[j++] = (byte)((s >> 8) & 0xFF);
            }
        }

        // Convert normalized float [-1,1] to 32-bit signed PCM little-endian (no dither needed for 32-bit).
        private static void FloatToPcm32(float[] src, byte[] dst, int count)
        {
            int j = 0;
            for (int i = 0; i < count; i++)
            {
                float v = src[i];
                if (v > 1f) v = 1f; else if (v < -1f) v = -1f;
                int s = (int)Math.Round(v * int.MaxValue);
                dst[j++] = (byte)(s & 0xFF);
                dst[j++] = (byte)((s >> 8) & 0xFF);
                dst[j++] = (byte)((s >> 16) & 0xFF);
                dst[j++] = (byte)((s >> 24) & 0xFF);
            }
        }

        // ===== Buffer helpers =====
        private static void EnsureCapacity(ref float[] buf, int needed)
        {
            if (buf == null) buf = new float[NextPow2(needed)];
            else if (buf.Length < needed) Array.Resize(ref buf, NextPow2(needed));
        }
        private static void EnsureCapacity(ref byte[] buf, int needed)
        {
            if (buf == null) buf = new byte[NextPow2(needed)];
            else if (buf.Length < needed) Array.Resize(ref buf, NextPow2(needed));
        }
        private static int NextPow2(int n)
        {
            if (n <= 0) return 256;
            n--; n |= n >> 1; n |= n >> 2; n |= n >> 4; n |= n >> 8; n |= n >> 16; n++;
            return n < 256 ? 256 : n;
        }

        private void EnsureRingCapacity(int roomNeeded)
        {
            if (_micRing.Length - _micCount >= roomNeeded) return;

            int newLen = NextPow2(_micCount + roomNeeded);
            if (newLen <= _micRing.Length) return;

            var newBuf = new float[newLen];
            for (int i = 0; i < _micCount; i++)
                newBuf[i] = _micRing[(_micR + i) % _micRing.Length];

            _micRing = newBuf;
            _micR = 0;
            _micW = _micCount % _micRing.Length;
        }

        // ===== Logging & utils =====
        private void OpenLog()
        {
            try
            {
                if (!string.IsNullOrEmpty(_logPath))
                {
                    TryDispose(ref _log);
                    Directory.CreateDirectory(Path.GetDirectoryName(_logPath)!);
                    _log = new StreamWriter(new FileStream(_logPath, FileMode.Append, FileAccess.Write, FileShare.Read))
                    { AutoFlush = true };
                    Info($"Log path: {_logPath}");
                }
            }
            catch { /* non-fatal */ }
        }
        private void Info(string msg)  => WriteLog("INFO",  msg);
        private void Warn(string msg)  => WriteLog("WARN",  msg);
        private void Error(string where, Exception ex) => WriteLog("ERROR", $"{where}: {ex}");

        private void WriteLog(string level, string msg, [CallerMemberName] string? where = null)
        {
            try
            {
                _log?.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {level} {where}: {msg}");
                Debug.WriteLine($"{level} {where}: {msg}");
            }
            catch { }
        }

        private static void TryStopDispose(ref WasapiLoopbackCapture? cap)
        {
            try { cap?.Stop(); } catch { }
            try { cap?.Dispose(); } catch { }
            cap = null;
        }
        private static void TryStopDispose(ref WasapiCapture? cap)
        {
            try { cap?.Stop(); } catch { }
            try { cap?.Dispose(); } catch { }
            cap = null;
        }
        private static void TryDispose<T>(ref T? obj) where T : class, IDisposable
        {
            try { obj?.Dispose(); } catch { }
            obj = null;
        }
        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        private void RaiseStatus(
            EngineStatusKind kind, string message, bool success = false,
            string? outputPathSystem = null, string? outputPathMic = null,
            string? outputPathMix = null, string? outputPathMp3 = null)
            => Status?.Invoke(this, new EngineStatusEventArgs(
                kind, message, success, outputPathSystem, outputPathMic, outputPathMix, outputPathMp3));

        // ===== Filename helper =====
        // Given a desired path, return a version that does not clobber existing files by appending
        // " (1)", " (2)", ... before the extension as needed.
        private static string UniquePath(string path)
        {
            try
            {
                if (!File.Exists(path)) return path;

                var dir = Path.GetDirectoryName(path)!;
                var name = Path.GetFileNameWithoutExtension(path);
                var ext  = Path.GetExtension(path);

                int i = 1;
                string candidate;
                do
                {
                    candidate = Path.Combine(dir, $"{name} ({i}){ext}");
                    i++;
                }
                while (File.Exists(candidate));
                return candidate;
            }
            catch
            {
                // If anything goes wrong, fall back to the original path (behavior prior to uniqueness).
                return path;
            }
        }
    }
}
