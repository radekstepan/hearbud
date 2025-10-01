// file: WasapiLoopMix/RecorderEngine.cs
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using CSCore;
using CSCore.Codecs.WAV;
using CSCore.CoreAudioAPI;
using CSCore.MediaFoundation; // MP3 encoder
using CSCore.SoundIn;
using CSCore.Streams;

namespace WasapiLoopMix
{
    public enum LevelSource { Mic, System }

    public sealed class LevelChangedEventArgs : EventArgs
    {
        public LevelChangedEventArgs(LevelSource source, float peak, bool clipped)
        { Source = source; Peak = peak; Clipped = clipped; }
        public LevelSource Source { get; }
        public float Peak { get; }
        public bool Clipped { get; }
    }

    public enum EngineStatusKind { Info, Error, Stopped }

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
        private int _outRate = 48000;
        private int _outChannels = 2;

        private const int BlockFrames = 1024;

        public double MicGain { get; set; } = 1.0;   // meters + mix balance
        public double LoopGain { get; set; } = 1.0;  // meters + mix balance

        public event EventHandler<LevelChangedEventArgs>? LevelChanged;
        public event EventHandler<EngineStatusEventArgs>? Status;

        private WasapiLoopbackCapture? _loopCap;
        private WasapiCapture? _micCap;

        private SoundInSource? _loopIn;
        private SoundInSource? _micIn;

        private ISampleSource? _loopSrc;
        private ISampleSource? _micSrc;

        private bool _monitoring;
        private bool _recording;

        // Writers / paths
        private WaveWriter? _wavSys;
        private WaveWriter? _wavMic;
        private WaveWriter? _wavMix;
        private string _pathSys = "";
        private string _pathMic = "";
        private string _pathMix = "";
        private string _pathMp3 = "";
        private int _kbps = 192;

        private string _logPath = "";
        private StreamWriter? _log;

        // Buffers
        private float[] _loopBufF     = new float[BlockFrames * 8];
        private float[] _micInBuf     = new float[BlockFrames * 8];
        private float[] _micConvBuf   = new float[BlockFrames * 8];
        private float[] _tmpMicBlock  = new float[BlockFrames * 8];
        private float[] _mixBufF      = new float[BlockFrames * 8];

        private byte[]  _pcm16Sys     = new byte[BlockFrames * 8 * 2];
        private byte[]  _pcm16Mic     = new byte[BlockFrames * 8 * 2];
        private byte[]  _pcm16Mix     = new byte[BlockFrames * 8 * 2];

        // Ring buffer to align mic to loopback for mix
        private readonly object _micRingLock = new();
        private float[] _micRing = new float[48000 * 4]; // ~2s stereo
        private int _micR, _micW, _micCount;

        // Loopback activity tracking (so mic-only sessions still produce files)
        private static readonly double _tickMs = 1000.0 / Stopwatch.Frequency;
        private long _lastLoopTick = 0;
        private const int LoopSilentMsThreshold = 200; // if no system frames for >200ms, drive mix from mic

        private volatile bool _disposed;

        // ===== Public API =====

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
                RaiseStatus(EngineStatusKind.Info, "Monitoring…");
                Info("Monitoring started");
            }
        }

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

            _pathSys = Path.Combine(outDir, $"{baseName}-system.wav");
            _pathMic = Path.Combine(outDir, $"{baseName}-mic.wav");
            _pathMix = Path.Combine(outDir, $"{baseName}-mix.wav");
            _pathMp3 = Path.Combine(outDir, $"{baseName}.mp3");
            TryDelete(_pathSys);
            TryDelete(_pathMic);
            TryDelete(_pathMix);
            TryDelete(_pathMp3);

            _logPath = Path.Combine(outDir, $"{baseName}.txt");
            OpenLog();

            Info("===== Session start =====");
            Info($"System WAV: {_pathSys}");
            Info($"Mic    WAV: {_pathMic}");
            Info($"Mix    WAV: {_pathMix}");
            Info($"Mix   MP3 : {_pathMp3} @ {_kbps}kbps");
            Info($"Loopback fmt: sr={_outRate}, ch={_outChannels}");

            _wavSys = new WaveWriter(_pathSys, new CSCore.WaveFormat(_outRate, 16, _outChannels));
            _wavMic = new WaveWriter(_pathMic, new CSCore.WaveFormat(_outRate, 16, _outChannels));
            _wavMix = new WaveWriter(_pathMix, new CSCore.WaveFormat(_outRate, 16, _outChannels));

            lock (_micRingLock) { _micR = _micW = _micCount = 0; }

            _recording = true;
            RaiseStatus(EngineStatusKind.Info, "Recording to WAV…");
            Info("Recording started");
        }

        public void Stop()
        {
            if (_disposed) return;

            // Close WAV writers first
            bool okSys = false, okMic = false, okMix = false, okMp3 = false;
            try { _wavSys?.Dispose(); okSys = File.Exists(_pathSys) && new FileInfo(_pathSys).Length > 0; } catch { }
            try { _wavMic?.Dispose(); okMic = File.Exists(_pathMic) && new FileInfo(_pathMic).Length > 0; } catch { }
            try { _wavMix?.Dispose(); okMix = File.Exists(_pathMix) && new FileInfo(_pathMix).Length > 0; } catch { }
            _wavSys = null; _wavMic = null; _wavMix = null;

            _recording = false;

            // Encode MP3 from the mix WAV (even if it’s mic-only mix)
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

            StopInternal(fullStop: false);

            // Consider success if ANY file has content (so user always sees the saved dialog)
            bool anyOk = okSys || okMic || okMix || okMp3;
            RaiseStatus(
                EngineStatusKind.Stopped,
                anyOk ? "Saved file(s)" : $"Recording stopped (files may be empty){(encEx != null ? " – MP3 encode failed" : "")}",
                success: anyOk,
                outputPathSystem: _pathSys, outputPathMic: _pathMic, outputPathMix: _pathMix, outputPathMp3: _pathMp3);

            Info("Recording stopped");
            Info("===== Session end =====");
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

            // Resolve devices
            MMDevice loopDev;
            MMDevice? micDev = null;
            using (var mmde = new MMDeviceEnumerator())
            {
                loopDev = mmde.GetDevice(opts.LoopbackDeviceId);
                if (!string.IsNullOrWhiteSpace(opts.MicDeviceId))
                    micDev = mmde.GetDevice(opts.MicDeviceId!);
            }

            Info($"Loopback: {loopDev?.FriendlyName}");
            if (micDev != null) Info($"Mic: {micDev.FriendlyName}"); else Info("Mic: (none)");

            // Loopback (defines output format)
            _loopCap = new WasapiLoopbackCapture { Device = loopDev };
            _loopCap.Initialize();
            _outRate     = _loopCap.WaveFormat.SampleRate;
            _outChannels = _loopCap.WaveFormat.Channels;

            _loopIn  = new SoundInSource(_loopCap) { FillWithZeros = true };
            _loopSrc = _loopIn.ToSampleSource();

            // Mic (required)
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

            // Log formats (best-effort)
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

                // meters
                float peakMic = 0f;
                for (int i = 0; i < conv; i++)
                {
                    float av = MathF.Abs((float)(_micConvBuf[i] * MicGain));
                    if (av > peakMic) peakMic = av;
                }
                LevelChanged?.Invoke(this, new LevelChangedEventArgs(LevelSource.Mic, peakMic, clipped:false));

                // Push to ring (for loopback-driven mixing)
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

                // Always write mic WAV
                if (_recording && _wavMic != null)
                {
                    EnsureCapacity(ref _pcm16Mic, conv * 2);
                    FloatToPcm16(_micConvBuf, _pcm16Mic, conv);
                    _wavMic.Write(_pcm16Mic, 0, conv * 2);
                }

                // If loopback is silent, *drive* system & mix from mic (system = zeros, mix = mic-only)
                if (_recording && _wavMix != null && _wavSys != null)
                {
                    long now = Stopwatch.GetTimestamp();
                    double msSinceLoop = (now - _lastLoopTick) * _tickMs;
                    if (msSinceLoop > LoopSilentMsThreshold)
                    {
                        // system zeros
                        EnsureCapacity(ref _pcm16Sys, conv * 2);
                        Array.Clear(_pcm16Sys, 0, conv * 2);
                        _wavSys.Write(_pcm16Sys, 0, conv * 2);

                        // mic-only mix (apply UI gain, average with zero = 0.5 * mic)
                        EnsureCapacity(ref _mixBufF, conv);
                        for (int i = 0; i < conv; i++)
                        {
                            float m = (float)(_micConvBuf[i] * MicGain);
                            float a = m * 0.5f; // consistent with (m + 0)/2 to keep headroom
                            if (a > 1f) a = 1f; else if (a < -1f) a = -1f;
                            _mixBufF[i] = a;
                        }
                        EnsureCapacity(ref _pcm16Mix, conv * 2);
                        FloatToPcm16(_mixBufF, _pcm16Mix, conv);
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

                // meters (system)
                float peakSys = 0f;
                for (int i = 0; i < gotLoop; i++)
                {
                    float av = MathF.Abs((float)(_loopBufF[i] * LoopGain));
                    if (av > peakSys) peakSys = av;
                }
                LevelChanged?.Invoke(this, new LevelChangedEventArgs(LevelSource.System, peakSys, clipped:false));

                // Write raw system WAV
                if (_recording && _wavSys != null)
                {
                    EnsureCapacity(ref _pcm16Sys, gotLoop * 2);
                    FloatToPcm16(_loopBufF, _pcm16Sys, gotLoop);
                    _wavSys.Write(_pcm16Sys, 0, gotLoop * 2);
                }

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
                if (micRead < gotLoop) Array.Clear(_tmpMicBlock, micRead, gotLoop - micRead);

                // Simple average mix with UI gain balance
                EnsureCapacity(ref _mixBufF, gotLoop);
                for (int i = 0; i < gotLoop; i++)
                {
                    float s = (float)(_loopBufF[i] * LoopGain);
                    float m = (float)(_tmpMicBlock[i] * MicGain);
                    float a = (s + m) * 0.5f; // -6 dB headroom
                    if (a > 1f) a = 1f; else if (a < -1f) a = -1f;
                    _mixBufF[i] = a;
                }

                if (_recording && _wavMix != null)
                {
                    EnsureCapacity(ref _pcm16Mix, gotLoop * 2);
                    FloatToPcm16(_mixBufF, _pcm16Mix, gotLoop);
                    _wavMix.Write(_pcm16Mix, 0, gotLoop * 2);
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

        private static void FloatToPcm16(float[] src, byte[] dst, int count)
        {
            int j = 0;
            for (int i = 0; i < count; i++)
            {
                float v = src[i];
                if (v > 1f) v = 1f; else if (v < -1f) v = -1f;
                short s = (short)Math.Round(v * short.MaxValue);
                dst[j++] = (byte)(s & 0xFF);
                dst[j++] = (byte)((s >> 8) & 0xFF);
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
    }
}
