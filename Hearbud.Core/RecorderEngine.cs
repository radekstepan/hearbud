// ----------------------------------------------------------------------------------------------------
// PURPOSE (Phase 4: NAudio v2 engine — the hearbud-b blueprint, fully landed)
// ----------------------------------------------------------------------------------------------------
// Records Windows system audio (WASAPI loopback) and microphone simultaneously,
// writing three RF64 float32 WAVs during capture:
//   1) <base>-system.wav  - raw system/loopback
//   2) <base>-mic.wav     - raw microphone (captured AT the target format)
//   3) <base>-mix.wav     - mix with user gains, DC blocking, kneed limiting
// After stopping, the mix is encoded to <base>.mp3 via Media Foundation.
//
// ARCHITECTURE (one format, one mixer, no lock pyramids)
// - L0 capture: 2× IAudioRecorder legs at 48 kHz float32 stereo (loopback +
//   raw-mode mic, IAudioClient3 low-latency, MMCSS "Pro Audio" on the Windows
//   WASAPI backend). Callbacks only copy → stamp (100-ns units, zero
//   conversion) → TryWrite. Never block, never throw: full queue drops
//   oldest + counts.
// - L1 handoff: bounded Channels (256/512, DropOldest, single-reader/writer).
// - L2 sync+DSP: ONE mixer thread (10 ms tick) drains by timestamp. Loopback
//   paces the session (pull all available); mic comes from TimestampedMicBuffer
//   (1 s horizon + PLL read rate). Loopback-idle + mic = mic-only pulls;
//   both idle = the session pauses (same file semantics as v1).
// - L3 persist: RF64 float WAVs to tmp (2 s header re-patch + WriteThrough),
//   atomically renamed on stop. Disk preflight fails fast; orphan sweep on boot.
// - L4 supervise: per-leg auto-rebuild with backoff (mixer coasts on zeros),
//   ~1 Hz HealthReport, per-leg Goertzel hum watch on the recorded mix,
//   engine-owned device notifications surfaced as DevicesChanged.
// The UI (or any host) only calls Monitor/Start/Stop and listens to events.
//
// WINDOWS GATES (could not run on this machine): 1-hour dual capture with zero
// callback overruns; yank/replug/default-switch mid-take without stopping;
// 8-hour soak with drift < ±2 ms; blind A/B vs Phase 3.
// ----------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Hearbud
{
    // Identifies which source a level meter event refers to.
    public enum LevelSource { Mic, System }

    // Event payload for real-time level updates to the UI.
    public sealed class LevelChangedEventArgs : EventArgs
    {
        public LevelChangedEventArgs(LevelSource source, float rms, float peak, bool clipped)
        { Source = source; Rms = rms; Peak = peak; Clipped = clipped; }
        public LevelSource Source { get; }
        public float Rms { get; }
        public float Peak { get; }
        public bool Clipped { get; }
    }

    public enum EngineStatusKind { Info, Error, Encoding, Stopped }

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
        public string OutputPath { get; set; } = "";
        // Empty (or whitespace) means mic-only: no loop leg is opened.
        public string LoopbackDeviceId { get; set; } = "";
        public string? MicDeviceId { get; set; }
        public bool IncludeMic { get; set; } = true;
        public int Mp3BitrateKbps { get; set; } = 192;
    }

    /// <summary>
    /// UI-safe audio endpoint description. The engine owns all NAudio device
    /// objects; hosts only ever see these DTOs plus <see cref="RecorderEngine.DevicesChanged"/>.
    /// </summary>
    public sealed record AudioDeviceInfo(string Id, string Name, bool IsDefault);

    public sealed class RecorderEngine : IDisposable
    {
        // One format everywhere: capture AT the target, no conversion anywhere.
        public const int TargetRate = 48000;
        public const int TargetCh = 2;
        private const int MixTickMs = 10;
        private const int TickFrames = TargetRate * MixTickMs / 1000; // 480
        private const int LoopQCap = 256;
        private const int MicQCap = 512;
        private const int MaxLoopFifoSec = 2;
        private const double Qpc100NsFreq = 10_000_000.0; // Backend stamp domain: 100-ns ticks (QPC on Windows)

        // One captured block + the 100-ns QPC tick it arrived on.
        private readonly record struct TimedBlock(float[] Samples, long Qpc100Ns, int Frames);

        private int _outRate = TargetRate;
        private int _outChannels = TargetCh;

        private double _micGain = 1.0;
        private double _loopGain = 1.0;

        /// <summary>Microphone gain. Volatile for lock-free UI↔mixer visibility.</summary>
        public double MicGain
        {
            get => Volatile.Read(ref _micGain);
            set => Volatile.Write(ref _micGain, value);
        }

        /// <summary>Loopback (system) gain. Volatile for lock-free UI↔mixer visibility.</summary>
        public double LoopGain
        {
            get => Volatile.Read(ref _loopGain);
            set => Volatile.Write(ref _loopGain, value);
        }

        /// <summary>True while recording to disk.</summary>
        public bool IsRecording => _recording;

        public event EventHandler<LevelChangedEventArgs>? LevelChanged;
        public event EventHandler<EngineStatusEventArgs>? Status;
        public event EventHandler<int>? EncodingProgress;
        public event EventHandler<HealthReport>? Health;
        /// <summary>Raised (on a backend thread — marshal to UI) when endpoints change.</summary>
        public event EventHandler? DevicesChanged;

        // Capture (the backend owns the audio threads; we only hold the legs).
        private readonly IAudioCaptureFactory _capture;
        private readonly IAudioEncoder _encoder;
        private IAudioRecorder? _loop;
        private IAudioRecorder? _mic;
        // Latched at open: the session includes a mic leg even after the leg
        // is stopped for the drain (StopInternal nulls _mic). The mixer keys
        // mic pulls off this, never off leg liveness, so stop-phase backlog
        // is mixed into the tail instead of dropped (and the drain ends).
        private bool _sessionHasMic;
        private Channel<TimedBlock>? _loopQ;
        private Channel<TimedBlock>? _micQ;

        // Mixer (single thread owns DSP + disk + FIFOs — no locks).
        private Task? _mixTask;
        private CancellationTokenSource? _mixCts;
        private readonly List<float> _loopFifo = new();
        private long _lastLoopQpc;
        private long _micNewestQpc;
        private readonly TimestampedMicBuffer _micBuffer = new(TargetRate, TargetCh, Qpc100NsFreq);
        private readonly DcBlocker _dcSys = new(TargetCh);
        private readonly DcBlocker _dcMicInMix = new(TargetCh);
        private readonly HumWatch _humWatchLoop = new(TargetRate);

        private bool _monitoring;
        private bool _recording;
        private bool _stopping;
        private int _recoveringLoop;
        private int _recoveringMic;

        private StreamingWavWriter? _wavSys;
        private StreamingWavWriter? _wavMic;
        private StreamingWavWriter? _wavMix;

        private string _pathSys = "";
        private string _pathMic = "";
        private string _pathMix = "";
        private string _pathMp3 = "";
        private int _kbps = 192;

        private string _tmpSys = "";
        private string _tmpMic = "";
        private string _tmpMix = "";

        private string _logPath = "";
        private StreamWriter? _log;
        private const long MaxLogSizeBytes = 10 * 1024 * 1024; // 10 MB
        private long _logBytesWritten = 0;
        private readonly object _logLock = new();

        // Mixer scratch (grown via EnsureCapacity, never per-block allocs).
        private float[] _sysBlock = new float[4096];
        private float[] _micBlock = new float[4096];
        private float[] _mixBlock = new float[4096];
        private byte[] _ioBytes = new byte[16384];

        private static readonly double _tickMs = 1000.0 / Stopwatch.Frequency;
        private long _sessionStartQpc;
        private long _mixedFrames;
        private long _lastHealthTick;
        private long _droppedBlocks;
        private long _micUnderrunBlocks;
        private long _mixBlockCounter;
        private double _lastMicBacklogSec;
        private double _micBacklogSecMax;
        private const int BacklogLogEveryNBlocks = 50;
        private bool _humWarned;
        private Exception? _mixerException;

        private const double LevelThrottleMs = 50.0;
        private long _lastLevelTickMic;
        private long _lastLevelTickSys;
        private float _peakSinceLastMic;
        private float _peakSinceLastSys;
        private double _rmsSumSinceLastMic;
        private double _rmsSumSinceLastSys;
        private long _rmsCountSinceLastMic;
        private long _rmsCountSinceLastSys;
        private bool _clippedSinceLastMic;
        private bool _clippedSinceLastSys;

        private RecorderStartOptions? _activeOpts;
        private string _loopDevName = "";
        private string _micDevName = "(none)";
        private string _activeLoopDeviceId = "";
        private string _activeMicDeviceId = "";

        private IDisposable? _deviceNotif;

        private volatile bool _disposed;

        public RecorderEngine(IAudioCaptureFactory? captureFactory = null, IAudioEncoder? encoder = null)
        {
            _capture = captureFactory ?? AudioCapture.Default;
            _encoder = encoder ?? AudioEncoder.Default;
        }

        // ===== Device enumeration (hosts use DTOs, never backend types) =====

        public static IReadOnlyList<AudioDeviceInfo> GetLoopbackDevices() =>
            AudioCapture.Default.GetLoopbackDevices();

        public static IReadOnlyList<AudioDeviceInfo> GetMicrophoneDevices() =>
            AudioCapture.Default.GetMicrophoneDevices();

        private void StartDeviceNotifications()
        {
            if (_deviceNotif != null) return;
            try
            {
                _deviceNotif = _capture.StartDeviceNotifications(FireDevicesChanged);
            }
            catch (Exception ex)
            {
                Error("StartDeviceNotifications", ex);
                _deviceNotif = null;
            }
        }

        private void FireDevicesChanged()
        {
            try { DevicesChanged?.Invoke(this, EventArgs.Empty); }
            catch { /* subscriber bugs must not kill backend callbacks */ }
        }

        private void StopDeviceNotifications()
        {
            TryDispose(ref _deviceNotif);
        }

        // ===== Public API =====

        public async Task MonitorAsync(RecorderStartOptions opts)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(RecorderEngine));
            // Never two mixer loops: a re-monitor (notably StartAsync during
            // auto-monitor) must retire the previous task first. Its token is
            // otherwise orphaned and the stale loop keeps racing the new one
            // on _loopFifo and the output files.
            await StopMixerAsync();
            StopInternal();
            ResetSessionState();
            await OpenCaptureAsync(opts);

            if (_loop != null)
            {
                _loop.BlockAvailable += OnLoopData;
                _loop.RecordingStopped += OnLoopStopped;
            }
            if (_mic != null) _mic.BlockAvailable += OnMicData;
            if (_mic != null) _mic.RecordingStopped += OnMicStopped;

            _loop?.StartRecording();
            _mic?.StartRecording();

            StartDeviceNotifications();

            _mixCts = new CancellationTokenSource();
            _mixTask = Task.Run(() => MixerLoopAsync(_mixCts.Token));

            _monitoring = true;

            RaiseStatus(EngineStatusKind.Info, "Monitoring…");
            Info("Monitoring started");
        }

        // Resets all per-session audio state. May only run with NO mixer task
        // alive (MonitorAsync stops it first): _loopFifo is mixer-thread-owned
        // and unsynchronized, so clearing it under a ticking mixer corrupts or
        // throws (List.CopyTo vs Clear). Covers monitoring and recording alike.
        private void ResetSessionState()
        {
            _micBuffer.Clear();
            _sessionHasMic = false;
            _loopFifo.Clear();
            _micUnderrunBlocks = 0;
            _mixBlockCounter = 0;
            _lastMicBacklogSec = 0.0;
            _micBacklogSecMax = 0.0;
            _sessionStartQpc = Stopwatch.GetTimestamp();
            _mixedFrames = 0;
            _droppedBlocks = 0;
            _mixerException = null;
            _humWarned = false;
            long now = Stopwatch.GetTimestamp();
            _lastLevelTickMic = _lastLevelTickSys = now;
            _lastHealthTick = now;
            _peakSinceLastMic = _peakSinceLastSys = 0f;
            _rmsSumSinceLastMic = _rmsSumSinceLastSys = 0.0;
            _rmsCountSinceLastMic = _rmsCountSinceLastSys = 0;
            _clippedSinceLastMic = _clippedSinceLastSys = false;
        }

        public async Task StartAsync(RecorderStartOptions opts)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(RecorderEngine));
            await MonitorAsync(opts);

            _kbps = opts.Mp3BitrateKbps;

            var basePath = string.IsNullOrWhiteSpace(opts.OutputPath)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "Recordings", $"rec-{DateTime.Now:yyyyMMdd_HHmmss}")
                : opts.OutputPath;

            var outDir = Path.GetDirectoryName(basePath)!;
            var baseName = Path.GetFileName(basePath);
            Directory.CreateDirectory(outDir);

            // Fail fast on a full disk instead of dying mid-take.
            var (verdict, freeBytes) = CrashSafeFiles.Preflight(
                outDir, CrashSafeFiles.EstimateBytesPerSecond(_outRate, _outChannels));
            if (!verdict.CanRecord)
                throw new InvalidOperationException($"Cannot start recording: {verdict.Warning} Free up space and try again.");

            _pathSys = UniquePath(Path.Combine(outDir, $"{baseName}-system.wav"));
            _pathMic = UniquePath(Path.Combine(outDir, $"{baseName}-mic.wav"));
            _pathMix = UniquePath(Path.Combine(outDir, $"{baseName}-mix.wav"));
            _pathMp3 = (_kbps > 0) ? UniquePath(Path.Combine(outDir, $"{baseName}.mp3")) : "";

            // Capture to tmp files; final names appear atomically on stop.
            _tmpSys = CrashSafeFiles.TmpPathFor(_pathSys);
            _tmpMic = CrashSafeFiles.TmpPathFor(_pathMic);
            _tmpMix = CrashSafeFiles.TmpPathFor(_pathMix);

            _logPath = UniquePath(Path.Combine(outDir, $"{baseName}.txt"));
            OpenLog();

            Info("===== Session start =====");
            Info($"Gains: LoopGain={LoopGain:F3}, MicGain={MicGain:F3}");
            Info($"Output System: {_pathSys}");
            Info($"Output Mic:    {_pathMic}");
            Info($"Output Mix:    {_pathMix}");
            Info($"Disk free: {freeBytes / 1024 / 1024} MB{(verdict.Warning != null ? $" ({verdict.Warning})" : "")}");
            if (verdict.Warning != null)
                RaiseStatus(EngineStatusKind.Info, verdict.Warning);

            // RF64 float32 everywhere — one format, no 4 GB wall.
            _wavSys = new StreamingWavWriter(_tmpSys, _outRate, _outChannels, 32, rf64: true);
            _wavMic = new StreamingWavWriter(_tmpMic, _outRate, _outChannels, 32, rf64: true);
            _wavMix = new StreamingWavWriter(_tmpMix, _outRate, _outChannels, 32, rf64: true);

            // Session state was reset inside MonitorAsync (above), where no
            // mixer task can be running yet.
            _recording = true;
            RaiseStatus(EngineStatusKind.Info, "Recording to WAV…");
            Info("Recording started");
        }

        public async Task StopAsync(CancellationToken cancellationToken = default)
        {
            if (_disposed) return;

            _recording = false;

            // 1. Stop capture; the mixer keeps running to drain the queues.
            StopInternal();

            if (_mixTask != null)
            {
                Info("Draining mixer…");
                long drainStart = Stopwatch.GetTimestamp();
                long deadline = Environment.TickCount64 + 30_000;
                while (Environment.TickCount64 < deadline && MixerHasPending())
                {
                    try { await Task.Delay(50, cancellationToken); }
                    catch (OperationCanceledException) { break; }
                }
                if (MixerHasPending())
                    Warn("Mixer drain timed out after 30s");
                await StopMixerAsync();
                Info($"Mixer drained in {Stopwatch.GetElapsedTime(drainStart).TotalMilliseconds:F0}ms");
            }

            if (_mixerException != null)
                RaiseStatus(EngineStatusKind.Error, $"Mixer failed: {_mixerException.Message}");

            // 2. Close the files (headers are already fresh from the 2 s re-patch),
            // then atomically publish tmp → final names.
            bool okSys = false, okMic = false, okMix = false, okMp3 = false;
            try { _wavSys?.Dispose(); } catch { }
            try { _wavMic?.Dispose(); } catch { }
            try { _wavMix?.Dispose(); } catch { }
            _wavSys = null; _wavMic = null; _wavMix = null;
            okSys = PublishTmp(ref _tmpSys, ref _pathSys, "system");
            okMic = PublishTmp(ref _tmpMic, ref _pathMic, "mic");
            okMix = PublishTmp(ref _tmpMix, ref _pathMix, "mix");

            // 3. MP3 encode (post-process, masters already safe). NAudio's helper
            // is synchronous with no progress/cancel, so 0 % → encode → 100 %.
            bool doMp3Encoding = _kbps > 0;
            Exception? encEx = null;
            if (doMp3Encoding)
            {
                RaiseStatus(EngineStatusKind.Encoding, "Encoding MP3…");
                EncodingProgress?.Invoke(this, 0);

                if (okMix && File.Exists(_pathMix))
                {
                    await Task.Run(() =>
                    {
                        try
                        {
                            Info($"Encoding MP3: {_pathMix} → {_pathMp3} @ {_kbps}kbps");
                            long encStart = Stopwatch.GetTimestamp();
                            okMp3 = _encoder.EncodeMp3(_pathMix, _pathMp3, _kbps);
                            Info($"MP3 encode ok={okMp3} in {Stopwatch.GetElapsedTime(encStart).TotalMilliseconds:F0}ms");
                        }
                        catch (Exception ex)
                        {
                            encEx = ex;
                            Error("MP3 encode", ex);
                        }
                    }, cancellationToken);
                }
                else
                {
                    Info("Mix WAV missing or empty; skipping MP3 encode.");
                }
                EncodingProgress?.Invoke(this, 100);
            }
            else
            {
                Info("Skipping MP3 encoding per settings.");
                _pathMp3 = "";
            }

            Info($"Mic underruns: {_micUnderrunBlocks}. Peak mic backlog: {_micBacklogSecMax:F4}s");
            if (_droppedBlocks > 0)
                Warn($"Dropped {_droppedBlocks} audio blocks (mixer backpressure)");
            Info("Recording stopped");
            Info("===== Session end =====");

            TryDispose(ref _log);
            _logPath = "";

            bool anyOk = okSys || okMic || okMix || okMp3;
            RaiseStatus(
                EngineStatusKind.Stopped,
                anyOk ? "Saved file(s)" : $"Recording stopped (empty){(encEx != null ? " – MP3 failed" : "")}",
                success: anyOk,
                outputPathSystem: _pathSys, outputPathMic: _pathMic, outputPathMix: _pathMix, outputPathMp3: _pathMp3);

            _pathSys = _pathMic = _pathMix = _pathMp3 = "";
        }

        public void StopMonitor()
        {
            StopInternal();
            // Best-effort synchronous mixer shutdown for the sync API.
            try { _mixCts?.Cancel(); _mixTask?.Wait(2000); }
            catch { }
            _mixTask = null;
            _mixCts?.Dispose();
            _mixCts = null;
            Info("Stopped.");
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try { _mixCts?.Cancel(); _mixTask?.Wait(2000); }
            catch { }
            try { _mixCts?.Dispose(); } catch { }

            StopInternal();

            try { _wavSys?.Dispose(); } catch { }
            try { _wavMic?.Dispose(); } catch { }
            try { _wavMix?.Dispose(); } catch { }
            _wavSys = null; _wavMic = null; _wavMix = null;

            StopDeviceNotifications();
            TryDispose(ref _log);
        }

        // ===== Capture lifecycle =====

        // Stops capture legs only; the mixer keeps running for the drain.
        private void StopInternal()
        {
            _stopping = true;
            try
            {
                if (_loop != null)
                {
                    try { _loop.BlockAvailable -= OnLoopData; } catch { }
                    try { _loop.RecordingStopped -= OnLoopStopped; } catch { }
                }
                if (_mic != null)
                {
                    try { _mic.BlockAvailable -= OnMicData; } catch { }
                    try { _mic.RecordingStopped -= OnMicStopped; } catch { }
                }
                StopDisposeRecorder(ref _loop);
                StopDisposeRecorder(ref _mic);
                try { _loopQ?.Writer.Complete(); } catch { }
                try { _micQ?.Writer.Complete(); } catch { }
                _monitoring = false;
                _activeLoopDeviceId = "";
                _activeMicDeviceId = "";
            }
            finally
            {
                _stopping = false;
            }
        }

        private async Task StopMixerAsync()
        {
            try { if (_mixCts != null) await _mixCts.CancelAsync(); } catch { }
            if (_mixTask != null)
            {
                try { await _mixTask.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (Exception ex) { Error("StopMixerAsync", ex); }
            }
            _mixTask = null;
            _mixCts?.Dispose();
            _mixCts = null;
        }

        // Mic-buffer frames below this count as drained: the buffer's
        // interpolator needs i0+1 < take, so its last frame is never
        // consumed and would otherwise pin every StopAsync to the 30 s
        // drain timeout. 8 frames ≈ 0.17 ms — an inaudible tail at worst.
        private const int MicDrainResidueFrames = 8;

        // Stop-phase drain predicate, split out for testing (see
        // ShouldKeepDraining test; MixerHasPending only feeds it live counts).
        private static bool ShouldKeepDraining(int loopQ, int micQ, int micAvailFrames, int loopFifoFloats) =>
            loopQ > 0 || micQ > 0 || micAvailFrames > MicDrainResidueFrames || loopFifoFloats > 0;

        // Advisory drain check (mixer owns the FIFO; a stale read just waits longer).
        private bool MixerHasPending()
        {
            try
            {
                int loopQ = _loopQ != null ? _loopQ.Reader.Count : 0;
                int micQ = _micQ != null ? _micQ.Reader.Count : 0;
                return ShouldKeepDraining(loopQ, micQ, _micBuffer.AvailableFrames, _loopFifo.Count);
            }
            catch { }
            return false;
        }

        private async Task OpenCaptureAsync(RecorderStartOptions opts)
        {
            _activeOpts = opts;
            Info("Opening devices…");

            // An empty loopback id means mic-only (macOS without a virtual
            // driver): the loop leg is skipped and the mixer runs its mic-only
            // pulls. A PRESENT but stale id still fails fast, as before.
            string? loopName = null;
            bool wantLoop = !string.IsNullOrWhiteSpace(opts.LoopbackDeviceId);
            if (wantLoop)
            {
                try
                {
                    loopName = _capture.GetDeviceName(opts.LoopbackDeviceId);
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(
                        $"Loopback device not found: {opts.LoopbackDeviceId}", ex);
                }
            }

            string? micName = null;
            if (opts.IncludeMic && !string.IsNullOrWhiteSpace(opts.MicDeviceId))
            {
                try
                {
                    micName = _capture.GetDeviceName(opts.MicDeviceId!);
                }
                catch (Exception ex)
                {
                    Info($"Mic device not found ({opts.MicDeviceId}); continuing loopback-only. {ex.Message}");
                }
            }

            _loopDevName = loopName ?? "(none)";
            _micDevName = micName ?? "(none)";

            // Loopback leg with backoff for startup races (fresh plug, BT reconnect).
            IAudioRecorder? loop = null;
            if (wantLoop)
            {
                Exception? lastError = null;
                for (int attempt = 0; attempt < 5; attempt++)
                {
                    try
                    {
                        loop = _capture.OpenLoopback(opts.LoopbackDeviceId, TargetRate, TargetCh,
                            Info, ex => Error("OnLoopData", ex));
                        lastError = null;
                        break;
                    }
                    catch (Exception ex)
                    {
                        lastError = ex;
                        if (attempt < 4)
                        {
                            int delayMs = Math.Min(250 << attempt, 2000);
                            Info($"Loopback open failed, retrying in {delayMs}ms (attempt {attempt + 1}/5)...");
                            await Task.Delay(delayMs);
                            // Re-resolve happens inside OpenLoopback: the endpoint
                            // may have been re-enumerated meanwhile.
                        }
                    }
                }
                if (loop == null)
                    throw new InvalidOperationException("Could not open loopback device.", lastError);
            }
            else
            {
                Info("Loopback leg disabled (mic-only session).");
            }

            // Mic leg (raw-mode-first with cooked fallback lives in the backend;
            // a build failure here still aborts the open, as before).
            IAudioRecorder? mic = null;
            if (micName != null)
            {
                try
                {
                    mic = _capture.OpenMicrophone(opts.MicDeviceId!, TargetRate, TargetCh,
                        Info, ex => Error("OnMicData", ex));
                }
                catch
                {
                    try { loop.Dispose(); } catch { }
                    throw;
                }
            }

            // Fresh handoff queues every session — no stale blocks across takes.
            // (Fifo/buffer/counter resets live in ResetSessionState, which runs
            // before the mixer task exists.)
            _loopQ = CaptureHandoff.Create<TimedBlock>(LoopQCap);
            _micQ = CaptureHandoff.Create<TimedBlock>(MicQCap);

            _loop = loop;
            _mic = mic;
            _sessionHasMic = mic != null;
            _activeLoopDeviceId = opts.LoopbackDeviceId;
            _activeMicDeviceId = opts.MicDeviceId ?? "";
        }

        private void RebuildLoop(RecorderStartOptions opts)
        {
            if (string.IsNullOrWhiteSpace(opts.LoopbackDeviceId)) return;
            if (_loop != null)
            {
                try { _loop.BlockAvailable -= OnLoopData; } catch { }
                try { _loop.RecordingStopped -= OnLoopStopped; } catch { }
            }
            StopDisposeRecorder(ref _loop);
            var rec = _capture.OpenLoopback(opts.LoopbackDeviceId, TargetRate, TargetCh,
                Info, ex => Error("OnLoopData", ex));
            _loop = rec;
            rec.BlockAvailable += OnLoopData;
            rec.RecordingStopped += OnLoopStopped;
            rec.StartRecording();
        }

        private void RebuildMic(RecorderStartOptions opts)
        {
            if (string.IsNullOrWhiteSpace(opts.MicDeviceId)) return;
            if (_mic != null)
            {
                try { _mic.BlockAvailable -= OnMicData; } catch { }
                try { _mic.RecordingStopped -= OnMicStopped; } catch { }
            }
            StopDisposeRecorder(ref _mic);
            var rec = _capture.OpenMicrophone(opts.MicDeviceId!, TargetRate, TargetCh,
                Info, ex => Error("OnMicData", ex));
            _mic = rec;
            rec.BlockAvailable += OnMicData;
            rec.RecordingStopped += OnMicStopped;
            rec.StartRecording();
        }

        // ===== Capture callbacks: stamp → hand off. NOTHING else. =====
        // These run on the backend's capture threads (NAudio MMCSS on Windows).
        // They must never block, touch the disk, resample, or throw — a throw
        // kills the capture thread.

        private void OnLoopData(object? sender, AudioBlockEventArgs e)
        {
            var q = _loopQ;
            if (q == null || _disposed) return;
            try
            {
                if (e.Frames <= 0) return;
                CaptureHandoff.TryWrite(q, new TimedBlock(e.Samples, e.Qpc100Ns, e.Frames), LoopQCap, ref _droppedBlocks);
            }
            catch (Exception ex) { Error("OnLoopData", ex); }
        }

        private void OnMicData(object? sender, AudioBlockEventArgs e)
        {
            var q = _micQ;
            if (q == null || _disposed) return;
            try
            {
                if (e.Frames <= 0) return;
                CaptureHandoff.TryWrite(q, new TimedBlock(e.Samples, e.Qpc100Ns, e.Frames), MicQCap, ref _droppedBlocks);
            }
            catch (Exception ex) { Error("OnMicData", ex); }
        }

        private void OnLoopStopped(object? sender, CaptureStoppedEventArgs e)
        {
            if (_stopping || _disposed || !_monitoring) return;
            if (e?.Error != null) Error("Loopback stopped", e.Error);
            _ = RecoverAsync(isLoop: true);
        }

        private void OnMicStopped(object? sender, CaptureStoppedEventArgs e)
        {
            if (_stopping || _disposed || !_monitoring) return;
            if (e?.Error != null) Error("Mic stopped", e.Error);
            _ = RecoverAsync(isLoop: false);
        }

        // Per-leg auto-rebuild with backoff. Never throws (fire-and-forget safe).
        // The mixer coasts on zeros meanwhile, so the session survives the gap.
        private async Task RecoverAsync(bool isLoop)
        {
            try
            {
                // One rebuild per leg at a time (no ref local: those can't cross awaits).
                bool mine = isLoop
                    ? Interlocked.Exchange(ref _recoveringLoop, 1) == 0
                    : Interlocked.Exchange(ref _recoveringMic, 1) == 0;
                if (!mine) return;
                try
                {
                    if (_disposed || _stopping || !_monitoring) return;
                    var opts = _activeOpts;
                    if (opts == null) return;
                    if (!isLoop && string.IsNullOrWhiteSpace(opts.MicDeviceId)) return;
                    if (isLoop && string.IsNullOrWhiteSpace(opts.LoopbackDeviceId)) return;

                    string name = isLoop ? "Loopback" : "Mic";
                    Info($"{name} leg lost — rebuilding…");
                    for (int attempt = 0; attempt < 8; attempt++)
                    {
                        if (_disposed || _stopping || !_monitoring) return;
                        await Task.Delay(250 * (1 << Math.Min(attempt, 4)));
                        try
                        {
                            if (isLoop) RebuildLoop(opts); else RebuildMic(opts);
                            Info($"{name} leg recovered");
                            RaiseStatus(EngineStatusKind.Info, $"{name} reconnected");
                            return;
                        }
                        catch (Exception ex)
                        {
                            Info($"{name} rebuild attempt {attempt + 1} failed: {ex.Message}");
                        }
                    }
                    Warn($"{name} leg failed to recover — continuing degraded");
                    RaiseStatus(EngineStatusKind.Error, $"{name} device lost — continuing without it");
                }
                finally
                {
                    if (isLoop) Volatile.Write(ref _recoveringLoop, 0);
                    else Volatile.Write(ref _recoveringMic, 0);
                }
            }
            catch (Exception ex)
            {
                Error("RecoverAsync", ex);
            }
        }

        // ===== Mixer: the single thread that owns DSP + disk =====

        private async Task MixerLoopAsync(CancellationToken ct)
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(MixTickMs));
            try
            {
                while (await timer.WaitForNextTickAsync(ct))
                {
                    try
                    {
                        MixerTick();
                    }
                    catch (Exception ex)
                    {
                        _mixerException = ex;
                        _recording = false;
                        Error("MixerLoop fatal", ex);
                        RaiseStatus(EngineStatusKind.Error, $"Mixer failed: {ex.Message}");
                        return;
                    }
                }
            }
            catch (OperationCanceledException) { /* clean stop */ }
        }

        private void MixerTick()
        {
            long nowTicks = Stopwatch.GetTimestamp();

            // Drain everything currently queued (never awaits audio).
            if (_loopQ != null)
                while (_loopQ.Reader.TryRead(out var lb))
                {
                    for (int i = 0; i < lb.Samples.Length; i++) _loopFifo.Add(lb.Samples[i]);
                    _lastLoopQpc = lb.Qpc100Ns;
                }
            int maxFifo = MaxLoopFifoSec * TargetRate * TargetCh;
            if (_loopFifo.Count > maxFifo)
            {
                _loopFifo.RemoveRange(0, _loopFifo.Count - maxFifo);
                _droppedBlocks++;
            }
            if (_micQ != null)
                while (_micQ.Reader.TryRead(out var mb))
                {
                    _micBuffer.Write(mb.Samples, mb.Samples.Length, mb.Qpc100Ns);
                    _micNewestQpc = mb.Qpc100Ns;
                }

            MaybeEmitHealth(nowTicks, _micBuffer.DepthSec);

            int loopFrames = _loopFifo.Count / TargetCh;
            if (loopFrames > 0)
            {
                // Loopback paces the session: pull everything available.
                int f = loopFrames;
                EnsureCapacity(ref _sysBlock, f * TargetCh);
                _loopFifo.CopyTo(0, _sysBlock, 0, f * TargetCh);
                _loopFifo.RemoveRange(0, f * TargetCh);
                EnsureCapacity(ref _micBlock, f * TargetCh);
                PullMic(f, _lastLoopQpc);
                ProcessAndWrite(f);
                AfterBlock(f);
            }
            else if (_sessionHasMic && _micBuffer.AvailableFrames > 0)
            {
                // Loopback idle but mic live: steady 10 ms mic-only pulls.
                int f = Math.Min(_micBuffer.AvailableFrames, TickFrames);
                EnsureCapacity(ref _sysBlock, f * TargetCh);
                Array.Clear(_sysBlock, 0, f * TargetCh);
                EnsureCapacity(ref _micBlock, f * TargetCh);
                PullMic(f, _micNewestQpc);
                ProcessAndWrite(f);
                AfterBlock(f);
            }
            // Else both idle: the session pauses (same file semantics as v1).
        }

        private void PullMic(int frames, long refQpc)
        {
            if (!_sessionHasMic)
            {
                Array.Clear(_micBlock, 0, frames * TargetCh);
                return;
            }
            _micBuffer.Read(_micBlock, 0, frames, refQpc, out int zeroFilled);
            if (zeroFilled > 0) _micUnderrunBlocks++;
        }

        private void ProcessAndWrite(int frames)
        {
            int count = frames * TargetCh;
            EnsureCapacity(ref _mixBlock, count);
            MixerDsp.ProcessMixBlock(_sysBlock, _micBlock, _mixBlock, count, TargetCh,
                LoopGain, MicGain, _dcSys, _dcMicInMix, _humWatchLoop,
                out float sysRms, out float sysPeak, out bool sysCl,
                out float micRms, out float micPeak, out bool micCl);

            // Meter accumulation with the usual 50 ms throttle to the UI.
            if (sysPeak > _peakSinceLastSys) _peakSinceLastSys = sysPeak;
            _rmsSumSinceLastSys += (double)sysRms * sysRms * count;
            _rmsCountSinceLastSys += count;
            _clippedSinceLastSys |= sysCl;
            if (micPeak > _peakSinceLastMic) _peakSinceLastMic = micPeak;
            _rmsSumSinceLastMic += (double)micRms * micRms * count;
            _rmsCountSinceLastMic += count;
            _clippedSinceLastMic |= micCl;

            long nowTicks = Stopwatch.GetTimestamp();
            if ((nowTicks - _lastLevelTickSys) * _tickMs >= LevelThrottleMs && _rmsCountSinceLastSys > 0)
            {
                float rms = (float)Math.Sqrt(_rmsSumSinceLastSys / _rmsCountSinceLastSys);
                LevelChanged?.Invoke(this, new LevelChangedEventArgs(LevelSource.System, rms, _peakSinceLastSys, _clippedSinceLastSys));
                _peakSinceLastSys = 0f;
                _rmsSumSinceLastSys = 0.0;
                _rmsCountSinceLastSys = 0;
                _clippedSinceLastSys = false;
                _lastLevelTickSys = nowTicks;
            }
            if ((nowTicks - _lastLevelTickMic) * _tickMs >= LevelThrottleMs && _rmsCountSinceLastMic > 0)
            {
                float rms = (float)Math.Sqrt(_rmsSumSinceLastMic / _rmsCountSinceLastMic);
                LevelChanged?.Invoke(this, new LevelChangedEventArgs(LevelSource.Mic, rms, _peakSinceLastMic, _clippedSinceLastMic));
                _peakSinceLastMic = 0f;
                _rmsSumSinceLastMic = 0.0;
                _rmsCountSinceLastMic = 0;
                _clippedSinceLastMic = false;
                _lastLevelTickMic = nowTicks;
            }

            if (_recording && _wavSys != null && _wavMic != null && _wavMix != null)
            {
                EnsureCapacity(ref _ioBytes, count * 4);
                Buffer.BlockCopy(_sysBlock, 0, _ioBytes, 0, count * 4);
                _wavSys.Write(_ioBytes, 0, count * 4);
                Buffer.BlockCopy(_micBlock, 0, _ioBytes, 0, count * 4);
                _wavMic.Write(_ioBytes, 0, count * 4);
                Buffer.BlockCopy(_mixBlock, 0, _ioBytes, 0, count * 4);
                _wavMix.Write(_ioBytes, 0, count * 4);
            }
        }

        private void AfterBlock(int frames)
        {
            _mixedFrames += frames;
            _mixBlockCounter++;
            double backlogSec = _micBuffer.DepthSec;
            _lastMicBacklogSec = backlogSec;
            if (backlogSec > _micBacklogSecMax) _micBacklogSecMax = backlogSec;
            if (_mixBlockCounter % BacklogLogEveryNBlocks == 0)
                Info($"Mic backlog: {backlogSec:F4} s (max {_micBacklogSecMax:F4} s)");
        }

        // ~1 Hz supervision signal. Cheap time check; the report is a snapshot.
        // Subscriber exceptions are swallowed — a UI bug must never kill audio.
        private void MaybeEmitHealth(long nowTicks, double backlogSec)
        {
            if ((nowTicks - _lastHealthTick) * _tickMs < EngineHealth.IntervalMs) return;
            _lastHealthTick = nowTicks;
            if (!_monitoring && !_recording) return;

            double driftMs = _sessionStartQpc == 0 ? 0.0
                : EngineHealth.ComputeDriftMs(_sessionStartQpc, nowTicks, _mixedFrames, _outRate);
            long dropped = Interlocked.Read(ref _droppedBlocks);
            var (humDetected, humFreq, _) = _humWatchLoop.Poll();
            var report = new HealthReport(
                dropped, _micUnderrunBlocks, backlogSec, _micBacklogSecMax,
                _mixBlockCounter, driftMs, DateTime.UtcNow, humDetected, humFreq);

            if (dropped > 0 || backlogSec > 0.25 || Math.Abs(driftMs) > 50.0)
                Warn($"Health: dropped={dropped} underruns={_micUnderrunBlocks} " +
                     $"backlog={backlogSec:F3}s drift={driftMs:F1}ms");
            if (humDetected && !_humWarned)
                Warn($"Mains hum detected @ {humFreq} Hz — see Troubleshooting Guide (Steady hum or buzz in the mic take)");
            _humWarned = humDetected;

            try { Health?.Invoke(this, report); } catch { }
        }

        // ===== Small helpers =====

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
            if (n <= 256) return 256;
            return (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)n);
        }

        private void OpenLog()
        {
            try
            {
                _logBytesWritten = 0;
                if (!string.IsNullOrEmpty(_logPath))
                {
                    TryDispose(ref _log);
                    Directory.CreateDirectory(Path.GetDirectoryName(_logPath)!);
                    _log = new StreamWriter(new FileStream(_logPath, FileMode.Append, FileAccess.Write, FileShare.Read))
                    { AutoFlush = true };
                    Info($"Log path: {_logPath}");
                }
            }
            catch { }
        }
        private void Info(string msg)  => WriteLog("INFO",  msg);
        private void Warn(string msg)  => WriteLog("WARN",  msg);
        private void Error(string where, Exception ex) => WriteLog("ERROR", $"{where}: {ex}");

        private void WriteLog(string level, string msg, [CallerMemberName] string? where = null)
        {
            try
            {
                lock (_logLock)
                {
                    if (_log == null) return;

                    var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {level} {where}: {msg}";
                    _logBytesWritten += line.Length * 2; // Rough estimate

                    if (_logBytesWritten > MaxLogSizeBytes)
                    {
                        _log.WriteLine("[LOG TRUNCATED - Maximum size reached]");
                        TryDispose(ref _log);
                        return;
                    }

                    _log.WriteLine(line);
                }
                Debug.WriteLine($"{level} {where}: {msg}");
            }
            catch { }
        }

        private static void StopDisposeRecorder(ref IAudioRecorder? rec)
        {
            try { rec?.StopRecording(); } catch { }
            try
            {
                if (rec is IAsyncDisposable ad)
                    ad.DisposeAsync().AsTask().Wait(2000);
                else rec?.Dispose();
            }
            catch { }
            rec = null;
        }

        private static void TryDispose<T>(ref T? obj) where T : class, IDisposable
        {
            try { obj?.Dispose(); } catch { }
            obj = null;
        }

        private void RaiseStatus(
            EngineStatusKind kind, string message, bool success = false,
            string? outputPathSystem = null, string? outputPathMic = null,
            string? outputPathMix = null, string? outputPathMp3 = null)
            => Status?.Invoke(this, new EngineStatusEventArgs(
                kind, message, success, outputPathSystem, outputPathMic, outputPathMix, outputPathMp3));

        // Best-effort atomic publish of one finished tmp file. Updates the path
        // to the name actually published (renumbered on collision).
        private bool PublishTmp(ref string tmp, ref string final, string leg)
        {
            try
            {
                if (string.IsNullOrEmpty(tmp) || !File.Exists(tmp)) return false;
                final = CrashSafeFiles.AtomicPublish(tmp, final);
                tmp = "";
                return new FileInfo(final).Length > 0;
            }
            catch (Exception ex)
            {
                Error($"Publish {leg}", ex);
                return false;
            }
        }

        // Kept for compatibility (existing tests exercise it via reflection);
        // delegates to the shared, tested helper.
        private static string UniquePath(string path) => CrashSafeFiles.EnsureUniquePath(path);
    }
}
