// ----------------------------------------------------------------------------------------------------
// PURPOSE
// ----------------------------------------------------------------------------------------------------
// This class records Windows system audio (WASAPI loopback) and, optionally, a microphone, in real-time.
// It writes three WAV files during capture:
//   1) <base>-system.wav  - the raw system/loopback stream
//   2) <base>-mic.wav     - the raw microphone stream (converted to match loopback format)
//   3) <base>-mix.wav     - summed mix of system + mic with user-controlled gains,
//                          peak-normalized only on overs (amix-style) + conditional limiter
// After stopping, it can optionally encode the mix.wav to MP3 (<base>.mp3) using Media Foundation.
// If "Original" quality is selected, it skips the MP3 step.
//
// OPTIMIZATIONS (PERFORMANCE FIXES)
// - ASYNC I/O: Writing to disk is now decoupled from the audio callback using a BlockingCollection queue.
//   This prevents disk latency (IO blocks) from stalling the sensitive audio threads.
//   The queue capacity is dynamically calculated to provide ~10s of buffer, adapting to the format.
// - BUFFER POOLING: We use ArrayPool<byte> to pass data to the writer thread. This avoids allocating
//   new byte[] objects 100 times a second, reducing Garbage Collector (GC) pressure significantly.
//   FIX: EnqueueWrite now uses try/finally to ensure rented buffers are returned even if adding fails.
// - ZERO-ALLOC RESAMPLING: The resampling logic now reuses a scratch buffer instead of allocating
//   temp arrays on every frame.
// - STOP TIMEOUT: StopAsync now includes a 30s timeout when draining the write queue to prevent
//   indefinite hangs if disk I/O stalls or the writer thread deadlocks.
// - ASYNC DEVICE INIT: OpenDevicesAsync now uses Task.Delay instead of Thread.Sleep. This prevents
//   the UI thread from freezing during audio device initialization or retry attempts.
// - CANCELLATION SUPPORT: StopAsync now accepts a CancellationToken to allow aborting 
//   the MP3 encoding process if it takes too long.
// - LOG LIMITING: The log file is now capped at 10MB to prevent unbounded growth during long sessions.
//
// DESIGN INTENT
// - Loopback (system) is the "clock source": whenever loopback provides a chunk, we pull a same-sized
//   chunk from a microphone ring buffer and mix them. This preserves relative timing and prevents
//   'line up at start' errors. If mic is late, we zero-fill the gap (so silence is kept).
// - If loopback is silent (e.g., user is speaking but no system audio), we still want files produced.
//   We track time since last loopback block and, if it exceeds a threshold, we *drive* the output using
//   the mic stream alone (system gets zeros, mix = scaled mic). This ensures mic-only sessions still work.
//
// ----------------------------------------------------------------------------------------------------

using System;
using System.Buffers; // Added: For ArrayPool (Memory Optimization)
using System.Collections.Concurrent; // Added: For BlockingCollection (Async I/O)
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading; 
using System.Threading.Tasks; 
using CSCore;
using CSCore.Codecs.WAV;
using CSCore.CoreAudioAPI;
using CSCore.MediaFoundation; 
using CSCore.SoundIn;
using CSCore.Streams;

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
        public string? LoopbackDeviceId { get; set; } = "";
        public string? MicDeviceId { get; set; }
        public bool IncludeMic { get; set; } = true; 
        public int Mp3BitrateKbps { get; set; } = 192;
        /// <summary>Optional hidden diagnostics: when set, raw float32 microphoneIn/speakerIn/mixOut dumps are written here.</summary>
        public string DumpDir { get; set; } = "";
        /// <summary>GAP-07: when false, only mix (+MP3) is written; skips *-system/-mic stems (1x I/O). Default true for compat.</summary>
        public bool WriteStems { get; set; } = true;
    }

    // --- ASYNC WRITER DEFINITIONS ---
    public enum AudioFileTarget { System, Mic, Mix }

    internal readonly struct AudioWriteJob
    {
        public readonly AudioFileTarget Target;
        public readonly byte[] Data; // Rented from pool
        public readonly int Count;

        public AudioWriteJob(AudioFileTarget target, byte[] data, int count)
        {
            Target = target;
            Data = data;
            Count = count;
        }
    }
    // --------------------------------

    public sealed class RecorderEngine : IDisposable
    {
        private int _outRate = 48000;
        private int _outChannels = 2;
        private const int BlockFrames = 1024;
        private const int DefaultQueueCapacity = 2000;
        private const int MaxQueueCapacity = 10000;

        private double _micGain = 1.0;
        private double _loopGain = 1.0;

        /// <summary>
        /// Gets or sets the microphone gain. 
        /// Thread-safe via Volatile.Read/Write to prevent torn reads on 32-bit systems
        /// and ensure visibility across UI and audio callback threads.
        /// </summary>
        public double MicGain 
        { 
            get => Volatile.Read(ref _micGain); 
            set => Volatile.Write(ref _micGain, value); 
        }

        /// <summary>
        /// Gets or sets the loopback (system) gain.
        /// Thread-safe via Volatile.Read/Write to prevent torn reads on 32-bit systems
        /// and ensure visibility across UI and audio callback threads.
        /// </summary>
        public double LoopGain 
        { 
            get => Volatile.Read(ref _loopGain); 
            set => Volatile.Write(ref _loopGain, value); 
        }

        private int _micMuted;
        private int _sysMuted;

        /// <summary>
        /// RecExperts parity (SetFileMicrophoneMute): per-frame mix mute.
        /// Zero-fills the mic channel in EmitFixedMix/EmitMicOnlyFixed.
        /// Mix + meters only — stem WAVs stay raw. Thread-safe mid-record toggle.
        /// </summary>
        public bool MicMuted
        {
            get => Volatile.Read(ref _micMuted) != 0;
            set { Volatile.Write(ref _micMuted, value ? 1 : 0); Info($"MicMuted={value} at masterFrames={_masterFramesEmitted}"); }
        }

        /// <summary>
        /// RecExperts parity (SetFileSpeakerMute): per-frame mix mute for system audio.
        /// </summary>
        public bool SystemMuted
        {
            get => Volatile.Read(ref _sysMuted) != 0;
            set { Volatile.Write(ref _sysMuted, value ? 1 : 0); Info($"SystemMuted={value} at masterFrames={_masterFramesEmitted}"); }
        }

        /// <summary>Single-call UI toggle so mic+sys flip together with one log line.</summary>
        public void SetChannelMute(bool micMuted, bool systemMuted)
        {
            Volatile.Write(ref _micMuted, micMuted ? 1 : 0);
            Volatile.Write(ref _sysMuted, systemMuted ? 1 : 0);
            Info($"Mute mic={micMuted} sys={systemMuted} at masterFrames={_masterFramesEmitted}");
        }

        /// <summary>
        /// Gets a value indicating whether the engine is currently recording to disk.
        /// </summary>
        public bool IsRecording => _recording;

        /// <summary>
        /// GAP-09: total mix bytes enqueued for writing this session (size-limit source).
        /// Reflection-friendly public getter; no behavior change.
        /// </summary>
        public long TotalMixBytesWritten => Interlocked.Read(ref _totalMixBytesWritten);

        /// <summary>
        /// GAP-09: total fixed frames emitted on the master clock this session.
        /// Reflection-friendly public getter; no behavior change.
        /// </summary>
        public long MasterFramesEmitted => Interlocked.Read(ref _masterFramesEmitted);

        /// <summary>
        /// GAP-09: wall-clock elapsed since the current recording started.
        /// Counts paused time too (simplest; pause does not stop the clock).
        /// Zero when not recording. Reflection-friendly public getter.
        /// </summary>
        public TimeSpan RecordElapsed
        {
            get
            {
                long start = Volatile.Read(ref _masterStartTick);
                if (!_recording || start == 0) return TimeSpan.Zero;
                double ms = (Stopwatch.GetTimestamp() - start) * _tickMs;
                return ms <= 0 ? TimeSpan.Zero : TimeSpan.FromMilliseconds(ms);
            }
        }

        /// <summary>
        /// GAP-09 #8: wall-clock elapsed of the most recently finished session,
        /// captured at StopAsync entry (RecordElapsed zeroes once stopping begins).
        /// Zero when no session has finished. Reflection-friendly public getter.
        /// </summary>
        public TimeSpan LastRecordElapsed { get; private set; }

        public event EventHandler<LevelChangedEventArgs>? LevelChanged;
        public event EventHandler<EngineStatusEventArgs>? Status;
        public event EventHandler<int>? EncodingProgress; 

        private WasapiLoopbackCapture? _loopCap;
        private WasapiCapture? _micCap;
        private SoundInSource? _loopIn;
        private SoundInSource? _micIn;
        private ISampleSource? _loopSrc;
        private ISampleSource? _micSrc;

        private bool _monitoring;
        private bool _recording;
        private volatile bool _paused;
        public bool IsPaused => _paused;

        // Writers are now accessed ONLY by the background thread
        private WaveWriter? _wavSys;
        private WaveWriter? _wavMic;
        private WaveWriter? _wavMix;
        private readonly bool _mixUse32Bit = true; 

        // Async Write Queue
        private BlockingCollection<AudioWriteJob>? _writeQueue;
        private Task? _writeTask;
        private long _droppedBlocks;
        private Exception? _writerException;
        private readonly WriterFaultTracker _faults = new();
        private int _queueHighWaterWarned;
        private volatile bool _diskFullStopRequested;
        public bool DiskFullStopRequested => _diskFullStopRequested;
        public string? FirstWriterError => _faults.FirstError?.Message;
        // GAP-09 auto-stop/split: cumulative mix payload bytes handed to the write
        // queue this session (WAV container overhead excluded; close enough for limits).
        private long _totalMixBytesWritten;

        private string _pathSys = "";
        private string _pathMic = "";
        private string _pathMix = "";
        private string _pathMp3 = "";
        private int _kbps = 192;
        private bool _writeStems = true;

        private string _logPath = "";
        private StreamWriter? _log;
        private const long MaxLogSizeBytes = 10 * 1024 * 1024; // 10 MB
        private long _logBytesWritten;

        // Buffers
        private float[] _loopBufF     = new float[BlockFrames * 8];
        private float[] _micInBuf     = new float[BlockFrames * 8];
        private float[] _micConvBuf   = new float[BlockFrames * 8];
        private float[] _tmpMicBlock  = new float[BlockFrames * 8];
        private float[] _mixBufF      = new float[BlockFrames * 8];
        
        // Reusable scratch buffer for the resampler (Performance Fix)
        private float[] _resampleScratch = new float[BlockFrames * 8];

        // Polyphase windowed-sinc table for ConvertToTargetChunked.
        // Table[p,k] = kernel value for fractional position p/32 at tap offset
        // d = (k - Half + 1) - p/32 (src-sample units, centre = 0).
        private static class SincPolyphase
        {
            public const int Phases = 32;
            public const int Taps = 12;
            public const int Half = Taps / 2;       // 6: group delay, src samples
            public const int HistFrames = Taps + 4; // 16: left-history depth
            public const double Cutoff = 0.90;      // x src-Nyquist
            public static readonly float[,] Table = BuildTable();

            private static float[,] BuildTable()
            {
                // 4-term Blackman-Harris window, -92 dB sidelobes.
                const double a0 = 0.35875, a1 = 0.48829, a2 = 0.14128, a3 = 0.01168;
                var t = new float[Phases, Taps];
                for (int p = 0; p < Phases; p++)
                {
                    double frac = (double)p / Phases;
                    double sum = 0.0;
                    for (int k = 0; k < Taps; k++)
                    {
                        double d = (k - Half + 1) - frac;
                        double x = Cutoff * d;
                        double sinc = Math.Abs(x) < 1e-9
                            ? 1.0
                            : Math.Sin(Math.PI * x) / (Math.PI * x);
                        double u = d / Half * 0.5 + 0.5; // [-Half,Half] -> [0,1]
                        if (u < 0.0) u = 0.0; else if (u > 1.0) u = 1.0;
                        double w = a0
                            - a1 * Math.Cos(2.0 * Math.PI * u)
                            + a2 * Math.Cos(4.0 * Math.PI * u)
                            - a3 * Math.Cos(6.0 * Math.PI * u);
                        double h = Cutoff * sinc * w;
                        t[p, k] = (float)h;
                        sum += h;
                    }
                    // Unity DC gain per phase: flat passband, no level shift.
                    float inv = (float)(1.0 / sum);
                    for (int k = 0; k < Taps; k++) t[p, k] *= inv;
                }
                return t;
            }
        }

        // GAP-02: stateful resampler (fractional phase carry, no per-chunk click/drift).
        // Only mic needs it; loopback is passthrough at _outRate/_outChannels.
        // Sinc version: PrevTail carries HistFrames of source history so the
        // 12-tap kernel sees identical neighbourhoods in chunked and single-shot
        // mode (bit-identical output); group delay Half is embraced as latency.
        private sealed class ResamplerState
        {
            public double NextSrcPos;
            public float[] PrevTail = new float[SincPolyphase.HistFrames * 8];
            public int PrevFrames;
            public int PrevCh;
            public long TotalSrcFrames;
            public long TotalDstFrames;
            public int SrcCh = -1;
            public int SrcRate;
            public int DstRate;
            public void Reset(int srcCh, int srcRate, int dstRate)
            {
                NextSrcPos = 0; PrevCh = 0; PrevFrames = 0; TotalSrcFrames = 0; TotalDstFrames = 0;
                SrcCh = srcCh; SrcRate = srcRate; DstRate = dstRate;
                int need = SincPolyphase.HistFrames * Math.Max(1, srcCh);
                if (PrevTail.Length < need) PrevTail = new float[need];
                else Array.Clear(PrevTail, 0, need);
            }
            // True FIR group delay: Half src samples -> dst frames.
            public double DelayDstFrames => SrcRate <= 0 ? 0 : SincPolyphase.Half * (double)DstRate / SrcRate;
        }
        private readonly ResamplerState _micResamp = new();
        internal double MicResamplerDelaySec => _micResamp.DelayDstFrames / Math.Max(1, _outRate);
        internal int MicResamplerDelayDstFrames => (int)Math.Ceiling(_micResamp.DelayDstFrames);

        private byte[]  _pcm16Sys     = new byte[BlockFrames * 8 * 2];
        private byte[]  _pcm16Mic     = new byte[BlockFrames * 8 * 2];
        private byte[]  _pcm16Mix     = new byte[BlockFrames * 8 * 2];
        private byte[]  _pcm32Mix     = new byte[BlockFrames * 8 * 4];

        private readonly object _micRingLock = new();
        private readonly object _loopRingLock = new();
        private readonly object _logLock = new();
        private float[] _micRing = new float[48000 * 4];
        private int _micR, _micW, _micCount;
        // GAP-01 fixed-frame dual-FIFO: loopback is no longer passthrough; both
        // sources land in bounded FIFOs (~2 s) and the mix emits fixed quanta.
        private float[] _loopRing = new float[48000 * 4];
        private int _loopR, _loopW, _loopCount;
        private long _loopOverrunBlocks;
        // Fixed emit quantum: 480 frames (10 ms @ 48 kHz) = 960 floats stereo.
        internal const int FixedFramesPerChannel = 480;
        private float[] _frameLoop = new float[FixedFramesPerChannel * 2];
        private float[] _frameMic = new float[FixedFramesPerChannel * 2];
        // Master clock: total fixed frames emitted + wall-clock anchor for resync.
        private long _masterFramesEmitted;
        private long _masterStartTick;
        private int _lagStreak;
        private long _resyncCount;
        internal const double ResyncThresholdSec = 0.08;
        internal const int ResyncStreakNeeded = 3;

        // TASK-3 continuous drift trim (PI corrector, RecExperts-style 1-2 ms).
        internal const double TrimDeadbandSec = 0.002;
        internal const double TrimTargetFrames = 1.0;
        internal const int TrimMinSamples = 8;
        internal const int TrimMaxSamples = 96;
        internal const int TrimZeroCrossWindow = 32;
        private const double TrimGainP = 0.25;
        private const double TrimGainI = 0.02;
        private double _driftIntegral;
        private const double DriftIntegralMax = 0.005;
        private long _trimTotalDropped;
        private long _trimTotalInserted;
        private long _trimTickDropped;
        private long _trimTickInserted;
        public long TrimDropped => Interlocked.Read(ref _trimTotalDropped);
        public long TrimInserted => Interlocked.Read(ref _trimTotalInserted);

        private static readonly double _tickMs = 1000.0 / Stopwatch.Frequency;
        private long _lastLoopTick;
        private long _lastMicTick;
        private long _captureGlitchBlocks;
        private const double CaptureStarvationMs = 50.0;
        internal const double CaptureWatchdogStarveMs = 2000.0;
        internal const double CaptureWatchdogCooldownSec = 10.0;
        private long _lastWatchdogTriggerTick;
        private long _watchdogRestarts;
        private readonly System.Collections.Concurrent.ConcurrentBag<int> _captureMmcssHandles =
            new System.Collections.Concurrent.ConcurrentBag<int>();

        /// <summary>Callback gaps &gt; 50 ms observed (WASAPI buffer-overwrite risk).</summary>
        public long CaptureGlitches => Interlocked.Read(ref _captureGlitchBlocks);

        /// <summary>Same-file reopen attempts triggered by the starvation watchdog.</summary>
        public long WatchdogRestarts => Interlocked.Read(ref _watchdogRestarts);

        /// <summary>
        /// Pure per-source staleness: lastTick==0 (no callback yet) falls back to the
        /// recording-start baseline so startup gets a grace period. baselineTick==0 → not stale.
        /// </summary>
        internal static bool IsCaptureStale(long nowTick, long lastTick, long baselineTick, double thresholdMs)
        {
            long b = lastTick != 0 ? lastTick : baselineTick;
            if (b == 0) return false;
            return (nowTick - b) * _tickMs >= thresholdMs;
        }

        /// <summary>
        /// RecExperts RestartGraph equivalent (decision only): while recording, if EVERY
        /// active source is callback-stale for &gt;CaptureWatchdogStarveMs, approve one
        /// same-file reopen. Pause-aware, disposal-safe, skips while a reopen is already
        /// in flight (_loopLost/_micLost, so _reopenCts ownership is never disturbed),
        /// rate-limited by CaptureWatchdogCooldownSec. Test hook: pass any nowTick.
        /// </summary>
        internal bool CheckCaptureStarvation(long nowTick)
        {
            if (!_recording || _paused || _disposed || !_monitoring) return false;
            bool loopActive = _loopCap != null || !string.IsNullOrEmpty(_activeLoopDeviceId);
            bool micActive = _micCap != null || !string.IsNullOrEmpty(_activeMicDeviceId);
            if (!loopActive && !micActive) return false;
            long start = Volatile.Read(ref _masterStartTick);
            long loopTick = Interlocked.Read(ref _lastLoopTick);
            long micTick = Interlocked.Read(ref _lastMicTick);
            bool loopStale = !loopActive || IsCaptureStale(nowTick, loopTick, start, CaptureWatchdogStarveMs);
            bool micStale = !micActive || IsCaptureStale(nowTick, micTick, start, CaptureWatchdogStarveMs);
            if (!(loopStale && micStale)) return false;
            lock (_devLock) { if (_loopLost || _micLost) return false; }
            long last = Interlocked.Read(ref _lastWatchdogTriggerTick);
            if (last != 0 && (nowTick - last) * _tickMs < CaptureWatchdogCooldownSec * 1000.0) return false;
            Interlocked.Exchange(ref _lastWatchdogTriggerTick, nowTick);
            return true;
        }

        /// <summary>
        /// 1 Hz UI-timer entry. On starvation: Warn, detach zombie(s), single
        /// TryResumeLostSourcesAsync (one CTS for both sources; cooldown re-triggers
        /// if still dead). WAV writers/headers never touched. Never throws.
        /// </summary>
        public void PollCaptureWatchdog()
        {
            try
            {
                if (!CheckCaptureStarvation(Stopwatch.GetTimestamp())) return;
                long n = Interlocked.Increment(ref _watchdogRestarts);
                Warn($"Capture watchdog: no callback from any active source for >{CaptureWatchdogStarveMs:F0}ms — same-file device reopen (attempt #{n}), WAV headers kept open.");
                bool loopActive = _loopCap != null || !string.IsNullOrEmpty(_activeLoopDeviceId);
                bool micActive = _micCap != null || !string.IsNullOrEmpty(_activeMicDeviceId);
                if (loopActive) { lock (_devLock) { _loopLost = true; } DetachSource(isLoop: true); }
                if (micActive) { lock (_devLock) { _micLost = true; } DetachSource(isLoop: false); }
                RaiseStatus(EngineStatusKind.Error, "Audio capture stalled — reopening devices in same file…");
                _ = TryResumeLostSourcesAsync();
            }
            catch { }
        }

        internal void RecordCallbackTiming(bool isLoop)
        {
            long now = Stopwatch.GetTimestamp();
            long prev = isLoop
                ? Interlocked.Exchange(ref _lastLoopTick, now)
                : Interlocked.Exchange(ref _lastMicTick, now);
            if (prev == 0) return;
            double gapMs = (now - prev) * _tickMs;
            if (gapMs > CaptureStarvationMs)
            {
                long n = Interlocked.Increment(ref _captureGlitchBlocks);
                Warn($"Capture starvation: {(isLoop ? "loopback" : "mic")} gap={gapMs:F1}ms (glitch #{n}) — late wakeup risks overwritten WASAPI buffer (click)");
            }
        }

        private void EnsureCaptureBoost()
        {
            try
            {
                if (NativeTiming.IsCaptureBoosted) return;
                int h = NativeTiming.TryBoostCaptureThread();
                if (h != 0) _captureMmcssHandles.Add(h);
            }
            catch { }
        }

        private void ReleaseCaptureMmcss()
        {
            try
            {
                while (_captureMmcssHandles.TryTake(out int h))
                {
                    try { NativeTiming.UnboostCaptureThread(h); } catch { }
                }
            }
            catch { }
        }
        private const int LoopSilentMsThreshold = 200; 

        private string _loopDevName = "";
        private string _micDevName  = "(none)";
        private string _activeLoopDeviceId = "";
        private string _activeMicDeviceId  = "";
        private long _micUnderrunBlocks;
        private long _micOverrunBlocks;
        private long _silencedSamples;
        private long _loopBlockCounter;
        private long _lastLagLogTick;
        private double _lastMicBacklogSec;
        private double _micBacklogSecMax;
        private bool _logTruncated;
        private const int BacklogLogEveryNBlocks = 50;
        private readonly object _devLock = new();
        private readonly object _dumpLock = new();
        private bool _loopLost;
        private bool _micLost;
        private CancellationTokenSource? _reopenCts;
        private int _preRollRemaining;

        // Monitor-restart gap probe: armed (Stopwatch tick) whenever a monitor
        // restart finishes; the first capture callback after that logs the
        // restart-to-first-audio gap. 0 = not armed.
        private long _restartTick;
        private int _restartLogged;

        // GAP-01: 1 ms system timer + MMCSS "Pro Audio" for our own threads.
        // CSCore owns the WASAPI callback threads, so we pin what we own (writer thread)
        // and raise timer resolution during capture; documented in ARCHITECTURE.
        // TASK-2: capture threads boost themselves via TryBoostCaptureThread (once per thread).
        private static class NativeTiming
        {
            [System.Runtime.InteropServices.DllImport("winmm.dll")]
            private static extern uint timeBeginPeriod(uint uPeriod);
            [System.Runtime.InteropServices.DllImport("winmm.dll")]
            private static extern uint timeEndPeriod(uint uPeriod);
            [System.Runtime.InteropServices.DllImport("avrt.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
            private static extern int AvSetMmThreadCharacteristicsW(string taskName, ref uint taskIndex);
            [System.Runtime.InteropServices.DllImport("avrt.dll")]
            private static extern bool AvRevertMmThreadCharacteristics(int handle);
            [System.Runtime.InteropServices.DllImport("kernel32.dll")]
            private static extern IntPtr GetCurrentThread();
            [System.Runtime.InteropServices.DllImport("kernel32.dll")]
            private static extern bool SetThreadPriority(IntPtr hThread, int nPriority);
            private const int THREAD_PRIORITY_TIME_CRITICAL = 15;

            private static int s_timerRef;

            private static readonly ThreadLocal<int> s_captureHandle =
                new ThreadLocal<int>(() => 0, trackAllValues: true);
            private static readonly ThreadLocal<string?> s_captureTask =
                new ThreadLocal<string?>(() => null, trackAllValues: true);

            public static bool IsCaptureBoosted => s_captureTask.Value != null;

            public static void Begin1Ms()
            {
                try
                {
                    if (!OperatingSystem.IsWindows()) return;
                    if (Interlocked.Increment(ref s_timerRef) == 1) _ = timeBeginPeriod(1);
                }
                catch { }
            }
            public static void End1Ms()
            {
                try
                {
                    if (!OperatingSystem.IsWindows()) return;
                    if (Interlocked.Decrement(ref s_timerRef) <= 0)
                    {
                        Interlocked.Exchange(ref s_timerRef, 0);
                        _ = timeEndPeriod(1);
                    }
                }
                catch { }
            }
            public static int BoostCurrentThread()
            {
                try
                {
                    if (!OperatingSystem.IsWindows()) return 0;
                    uint idx = 0;
                    return AvSetMmThreadCharacteristicsW("Pro Audio", ref idx);
                }
                catch { return 0; }
            }
            public static void Unboost(int handle)
            {
                try { if (OperatingSystem.IsWindows() && handle != 0) _ = AvRevertMmThreadCharacteristics(handle); } catch { }
            }

            public static int TryBoostCaptureThread()
            {
                try
                {
                    if (!OperatingSystem.IsWindows()) return 0;
                    if (s_captureTask.Value != null) return s_captureHandle.Value;
                    int h = 0;
                    try
                    {
                        uint idx = 0;
                        h = AvSetMmThreadCharacteristicsW("Pro Audio", ref idx);
                    }
                    catch { h = 0; }
                    try { SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_TIME_CRITICAL); } catch { }
                    if (h != 0)
                    {
                        s_captureHandle.Value = h;
                        s_captureTask.Value = "Pro Audio";
                    }
                    return h;
                }
                catch { return 0; }
            }

            public static void UnboostCaptureThread(int handle)
            {
                try
                {
                    if (OperatingSystem.IsWindows() && handle != 0)
                        _ = AvRevertMmThreadCharacteristics(handle);
                }
                catch { }
                finally
                {
                    try
                    {
                        if (s_captureHandle.IsValueCreated && s_captureHandle.Value == handle)
                            s_captureHandle.Value = 0;
                    }
                    catch { }
                    try
                    {
                        if (s_captureTask.IsValueCreated && s_captureHandle.Value == 0)
                            s_captureTask.Value = null;
                    }
                    catch { }
                }
            }
        }
        private FileStream? _dumpMic;
        private FileStream? _dumpSys;
        private FileStream? _dumpMix;

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

        private volatile bool _disposed;

        // ===== Public API =====

        public async Task MonitorAsync(RecorderStartOptions opts)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            long t0 = Stopwatch.GetTimestamp();
            CrashLog.LogOnly("AudioEngine",
                $"t={DateTime.Now:HH:mm:ss.fff} MonitorAsync entry loop={(string.IsNullOrWhiteSpace(opts.LoopbackDeviceId) ? "-" : "set")} mic={(string.IsNullOrWhiteSpace(opts.MicDeviceId) ? "-" : "set")} monitoring={_monitoring} recording={_recording} tid={Environment.CurrentManagedThreadId}");
            StopInternal(fullStop: true);
            CrashLog.LogOnly("AudioEngine",
                $"t={DateTime.Now:HH:mm:ss.fff} MonitorAsync StopInternal done +{(Stopwatch.GetTimestamp() - t0) * _tickMs:F0}ms");
            await OpenDevicesAsync(opts);

            if (!_monitoring)
            {
                if (_loopIn != null) _loopIn.DataAvailable += OnLoopbackData;
                if (_micIn != null) _micIn.DataAvailable += OnMicData;

                _loopCap?.Start();
                _micCap?.Start();

                _monitoring = true;
                _lastLoopTick = Stopwatch.GetTimestamp();
                _lastLevelTickMic = _lastLevelTickSys = _lastLoopTick; 
                _peakSinceLastMic = _peakSinceLastSys = 0f;
                _rmsSumSinceLastMic = _rmsSumSinceLastSys = 0.0;
                _rmsCountSinceLastMic = _rmsCountSinceLastSys = 0;
                _clippedSinceLastMic = _clippedSinceLastSys = false;

                Volatile.Write(ref _restartLogged, 0);
                Volatile.Write(ref _restartTick, Stopwatch.GetTimestamp());
                CrashLog.LogOnly("AudioEngine",
                    $"t={DateTime.Now:HH:mm:ss.fff} MonitorAsync caps started, total +{(Stopwatch.GetTimestamp() - t0) * _tickMs:F0}ms");

                RaiseStatus(EngineStatusKind.Info, "Monitoring…");
                Info("Monitoring started");
            }
        }

        /// <summary>
        /// Idle include-toggle path. Unchecked sides are muted in software
        /// (samples gated to zero, meters read 0) instead of being closed:
        /// a click never calls WASAPI Stop/Initialize/Start, so the driver is
        /// never disturbed and the surviving capture keeps running. A wanted
        /// side that is not open yet (first check after launch, or a changed
        /// device) is opened; an unwanted side is never torn down here.
        /// Never disturbs a running recording (no-op when recording). Safe to
        /// call from any thread; device COM work happens on the caller's thread.
        /// </summary>
        public Task MonitorSourcesAsync(RecorderStartOptions opts)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            long t0 = Stopwatch.GetTimestamp();
            void log(string m) => CrashLog.LogOnly("AudioEngine",
                $"t={DateTime.Now:HH:mm:ss.fff} +{(Stopwatch.GetTimestamp() - t0) * _tickMs:F0}ms tid={Environment.CurrentManagedThreadId} {m}");
            bool wantLoop = !string.IsNullOrWhiteSpace(opts.LoopbackDeviceId);
            bool wantMic = !string.IsNullOrWhiteSpace(opts.MicDeviceId);
            log($"MonitorSources entry wantLoop={wantLoop} wantMic={wantMic} monitoring={_monitoring} recording={_recording}");
            if (_recording || _disposed) { log("abort: recording or disposed"); return Task.CompletedTask; }
            try
            {
                // Soft mute first: instant in the meters, no device I/O.
                SetChannelMute(micMuted: !wantMic, systemMuted: !wantLoop);

                bool loopMismatch = wantLoop
                    ? (_loopCap == null || _loopCap.Device.DeviceID != opts.LoopbackDeviceId)
                    : false;
                bool micMismatch = wantMic
                    ? (_micCap == null || _micCap.Device.DeviceID != opts.MicDeviceId)
                    : false;
                if (!loopMismatch && !micMismatch) { log("no change, survivor untouched"); return Task.CompletedTask; }

                if (loopMismatch)
                {
                    // Only reached when a wanted side is missing or its device
                    // changed; unchecking never comes here.
                    long s0 = Stopwatch.GetTimestamp();
                    try { if (_loopIn != null) _loopIn.DataAvailable -= OnLoopbackData; } catch { }
                    TryStopDispose(ref _loopCap);
                    TryDispose(ref _loopIn);
                    TryDispose(ref _loopSrc);
                    lock (_loopRingLock) { _loopR = _loopW = _loopCount = 0; }
                    log($"loop side torn down +{(Stopwatch.GetTimestamp() - s0) * _tickMs:F0}ms");
                    MMDevice loopDev;
                    using (var mmde = new MMDeviceEnumerator()) loopDev = mmde.GetDevice(opts.LoopbackDeviceId!);
                    log($"loop GetDevice ok: {loopDev.FriendlyName}");
                    var cap = new WasapiLoopbackCapture { Device = loopDev };
                    cap.Initialize();
                    log("loop Initialize ok");
                    _loopCap = cap;
                    _outRate = _loopCap.WaveFormat.SampleRate;
                    _outChannels = _loopCap.WaveFormat.Channels;
                    _loopIn = new SoundInSource(_loopCap) { FillWithZeros = true };
                    _loopSrc = _loopIn.ToSampleSource();
                    _loopIn.DataAvailable += OnLoopbackData;
                    _loopCap.Start();
                    _lastLoopTick = Stopwatch.GetTimestamp();
                    log($"loop restarted sr={_outRate} ch={_outChannels}");
                }

                if (_recording || _disposed) { log("abort mid-way: recording or disposed"); return Task.CompletedTask; }

                if (micMismatch)
                {
                    // As above: only a missing/changed wanted source, never a mute.
                    long s0 = Stopwatch.GetTimestamp();
                    try { if (_micIn != null) _micIn.DataAvailable -= OnMicData; } catch { }
                    TryStopDispose(ref _micCap);
                    TryDispose(ref _micIn);
                    TryDispose(ref _micSrc);
                    lock (_micRingLock) { _micR = _micW = _micCount = 0; }
                    try { Array.Clear(_resampleScratch, 0, _resampleScratch.Length); } catch { }
                    log($"mic side torn down +{(Stopwatch.GetTimestamp() - s0) * _tickMs:F0}ms");
                    MMDevice micDev;
                    using (var mmde = new MMDeviceEnumerator()) micDev = mmde.GetDevice(opts.MicDeviceId!);
                    log($"mic GetDevice ok: {micDev.FriendlyName}");
                    var cap = new WasapiCapture { Device = micDev };
                    cap.Initialize();
                    log("mic Initialize ok");
                    _micCap = cap;
                    _micIn = new SoundInSource(_micCap) { FillWithZeros = true };
                    _micSrc = _micIn.ToSampleSource();
                    lock (_micRingLock) { _micR = _micW = _micCount = 0; }
                    _micResamp.Reset(_micSrc.WaveFormat.Channels, _micSrc.WaveFormat.SampleRate, _outRate);
                    _micIn.DataAvailable += OnMicData;
                    _micCap.Start();
                    log("mic restarted");
                }

                _activeLoopDeviceId = _loopCap == null ? "" : _loopCap.Device.DeviceID;
                _activeMicDeviceId = _micCap == null ? "" : _micCap.Device.DeviceID;
                _monitoring = _loopCap != null || _micCap != null;
                if (_monitoring)
                {
                    Volatile.Write(ref _restartLogged, 0);
                    Volatile.Write(ref _restartTick, Stopwatch.GetTimestamp());
                }
                log($"done monitoring={_monitoring}");
                return Task.CompletedTask;
            }
            catch (Exception ex)
            {
                log($"EXCEPTION {ex.GetType().Name}: {ex.Message}");
                throw;
            }
        }

        public async Task StartAsync(RecorderStartOptions opts)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await MonitorAsync(opts); 

            // Idle include toggles mute in software; a recording starts from
            // the checkbox-derived device set, so included sides are live.
            SetChannelMute(micMuted: false, systemMuted: false);

            _kbps = opts.Mp3BitrateKbps;
            _writeStems = opts.WriteStems;

            var basePath = string.IsNullOrWhiteSpace(opts.OutputPath)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "Recordings", $"rec-{DateTime.Now:yyyyMMdd_HHmmss}")
                : opts.OutputPath;

            var outDir = Path.GetDirectoryName(basePath)!;
            var baseName = Path.GetFileName(basePath);
            Directory.CreateDirectory(outDir);

            _pathSys = UniquePath(Path.Combine(outDir, $"{baseName}-system.wav"));
            _pathMic = UniquePath(Path.Combine(outDir, $"{baseName}-mic.wav"));
            _pathMix = UniquePath(Path.Combine(outDir, $"{baseName}-mix.wav"));
            _pathMp3 = (_kbps > 0) ? UniquePath(Path.Combine(outDir, $"{baseName}.mp3")) : "";

            _logPath = UniquePath(Path.Combine(outDir, $"{baseName}.txt"));
            OpenLog();

            Info("===== Session start =====");
            Info($"Gains: LoopGain={LoopGain:F3}, MicGain={MicGain:F3}");
            Info($"Output System: {_pathSys}");
            Info($"Output Mic:    {_pathMic}");
            Info($"Output Mix:    {_pathMix}");

            // Create writers (stems optional: mix-only cuts write I/O ~3x→1x)
            if (_writeStems)
            {
                _wavSys = new WaveWriter(_pathSys, new CSCore.WaveFormat(_outRate, 16, _outChannels));
                _wavMic = new WaveWriter(_pathMic, new CSCore.WaveFormat(_outRate, 16, _outChannels));
            }
            else
            {
                _wavSys = null; _wavMic = null;
                _pathSys = ""; _pathMic = "";
                Info("Stems disabled: writing mix only.");
            }
            _wavMix = new WaveWriter(_pathMix, new CSCore.WaveFormat(_outRate, _mixUse32Bit ? 32 : 16, _outChannels));

            // Initialize Async Write Queue and Task
            // Capacity is set to handle ~10 seconds of buffering if disk stalls completely.
            _writeQueue = new BlockingCollection<AudioWriteJob>(
                Math.Clamp(CalculateOptimalQueueSize(), DefaultQueueCapacity, MaxQueueCapacity)); 
            _writeTask = Task.Factory.StartNew(DiskWriteLoop, TaskCreationOptions.LongRunning);

            lock (_micRingLock) { _micR = _micW = _micCount = 0; }
            lock (_loopRingLock) { _loopR = _loopW = _loopCount = 0; }
            _micResamp.Reset(-1, 0, 0);
            _micUnderrunBlocks = 0;
            _micOverrunBlocks = 0;
            _loopOverrunBlocks = 0;
            _lastMicTick = 0;
            Interlocked.Exchange(ref _captureGlitchBlocks, 0);
            Interlocked.Exchange(ref _lastWatchdogTriggerTick, 0);
            Interlocked.Exchange(ref _watchdogRestarts, 0);
            _silencedSamples = 0;
            _loopBlockCounter = 0;
            _masterFramesEmitted = 0;
            _masterStartTick = Stopwatch.GetTimestamp();
            _totalMixBytesWritten = 0;
            _lagStreak = 0;
            _resyncCount = 0;
            _preRollRemaining = 2;
            _lastLagLogTick = 0;
            _lastMicBacklogSec = 0.0;
            _micBacklogSecMax = 0.0;
            _logTruncated = false;
            _faults.Reset();
            _queueHighWaterWarned = 0;
            _diskFullStopRequested = false;
            NativeTiming.Begin1Ms();
            OpenDumpStreams(opts.DumpDir, outDir, baseName);
            try { SessionManifest.Begin(outDir, baseName, _pathSys, _pathMic, _pathMix, _pathMp3, _outRate, _outChannels, _mixUse32Bit ? 32 : 16); } catch { }

            _paused = false;
            _recording = true;
            RaiseStatus(EngineStatusKind.Info, "Recording to WAV…");
            Info("Recording started");
        }

        public void Pause()
        {
            if (!_recording || _paused) return;
            _paused = true;
            Info($"Paused at masterFrames={_masterFramesEmitted}");
            RaiseStatus(EngineStatusKind.Info, "Paused — silence while paused");
        }

        public void Resume()
        {
            if (!_recording || !_paused) return;
            _paused = false;
            _lastLoopTick = Stopwatch.GetTimestamp();
            _lastMicTick = _lastLoopTick;
            _lagStreak = 0;
            Info($"Resumed at masterFrames={_masterFramesEmitted}");
            RaiseStatus(EngineStatusKind.Info, "Recording to WAV…");
        }

        public async Task StopAsync(CancellationToken cancellationToken = default)
        {
            if (_disposed) return;

            // GAP-09 #8: snapshot for history before _recording flips (RecordElapsed zeroes after).
            try { LastRecordElapsed = RecordElapsed; } catch { LastRecordElapsed = TimeSpan.Zero; }

            _recording = false;
            _paused = false;
            lock (_devLock) { _reopenCts?.Cancel(); _reopenCts = null; _loopLost = _micLost = false; }

            StopInternal(fullStop: false);

            // 1. Drain the write queue.
            // We tell the queue no more items are coming, then wait for the background thread
            // to finish writing everything currently in the buffer.
            if (_writeQueue != null && _writeTask != null)
            {
                Info("Finishing background writes...");
                try { _writeQueue.CompleteAdding(); }
                catch (InvalidOperationException) { /* writer thread already completed it (disk-full path) */ }

                // Wait for background writes to finish, but with a timeout to prevent hanging
                // if disk I/O stalls or the writer thread deadlocks.
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                try
                {
                    await _writeTask.WaitAsync(cts.Token);
                }
                catch (OperationCanceledException)
                {
                    Warn("Write queue drain timed out after 30s");
                }

                _writeQueue.Dispose();
                _writeQueue = null;
            }

            // Check for writer exceptions and surface them to the UI
            if (_writerException != null)
            {
                RaiseStatus(EngineStatusKind.Error, $"Disk write failed: {_writerException.Message}");
            }
            if (_faults.TotalErrors > 0)
            {
                Warn($"Write errors this session: sys={_faults.SysErrors} mic={_faults.MicErrors} mix={_faults.MixErrors} " +
                     $"first=({_faults.FirstTarget}) {_faults.FirstError?.Message}");
                if (_writerException == null)
                    RaiseStatus(EngineStatusKind.Error,
                        $"Disk write failed ({_faults.TotalErrors} block(s), first {_faults.FirstTarget}): {_faults.FirstError?.Message}");
            }

            // 2. Now it is safe to close the files (WAV headers are updated on Dispose).
            // GAP-06: shared finalize path so abort/Dispose leaves valid headers too.
            bool okSys, okMic, okMix;
            bool okMp3 = false;
            (okSys, okMic, okMix) = FinalizeWavFiles();
            
            bool doMp3Encoding = _kbps > 0;
            Exception? encEx = null;

            // 3. Perform MP3 encoding (post-process)
            if (doMp3Encoding)
            {
                RaiseStatus(EngineStatusKind.Encoding, "Encoding MP3…");

                if (okMix && File.Exists(_pathMix))
                {
                    await Task.Run(() =>
                    {
                        try
                        {
                            Info($"Encoding MP3: {_pathMix} → {_pathMp3} @ {_kbps}kbps");
                            using var reader = new WaveFileReader(_pathMix);

                            IWaveSource source = reader;
                            bool sourceNeedsDispose = false;
                            CSCore.WaveFormat targetFormat = reader.WaveFormat;

                            if (reader.WaveFormat.BitsPerSample != 16)
                            {
                                Info($"Converting {reader.WaveFormat.BitsPerSample}-bit to 16-bit for MP3 encoder");
                                var sampleSrc = reader.ToSampleSource();
                                source = sampleSrc.ToWaveSource(16);
                                sourceNeedsDispose = true;
                                targetFormat = source.WaveFormat;
                            }

                            try
                            {
                                using var encoder = MediaFoundationEncoder.CreateMP3Encoder(targetFormat, _pathMp3, _kbps * 1000);
                                long totalBytes = reader.Length;
                                long bytesProcessed = 0;
                                byte[] buf = new byte[1 << 16]; 
                                int read;

                                while ((read = source.Read(buf, 0, buf.Length)) > 0)
                                {
                                    cancellationToken.ThrowIfCancellationRequested();
                                    encoder.Write(buf, 0, read);
                                    bytesProcessed += read;
                                    if (totalBytes > 0)
                                    {
                                        int percent = Math.Min(100, (int)((double)bytesProcessed * 100 / (totalBytes / (reader.WaveFormat.BitsPerSample / 16))));
                                        EncodingProgress?.Invoke(this, percent);
                                    }
                                }
                            }
                            finally
                            {
                                if (sourceNeedsDispose && source is IDisposable d) d.Dispose();
                            }

                            okMp3 = File.Exists(_pathMp3) && new FileInfo(_pathMp3).Length > 0;
                            Info($"MP3 encode ok={okMp3}");
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
            }
            else
            {
                Info("Skipping MP3 encoding per settings.");
                _pathMp3 = "";
            }

            Info($"Mic ring underruns: {_micUnderrunBlocks}. Overruns: {_micOverrunBlocks}. Silenced samples: {_silencedSamples}. Peak mic backlog: {_micBacklogSecMax:F4}s");
            if (_droppedBlocks > 0)
            {
                Warn($"Dropped {_droppedBlocks} audio blocks due to disk I/O stall");
            }
            Info("Recording stopped");
            Info("===== Session end =====");

            try
            {
                string? dir = null, bn = null;
                try
                {
                    string anchor = !string.IsNullOrEmpty(_pathMix) ? _pathMix : (!string.IsNullOrEmpty(_pathSys) ? _pathSys : _pathMic);
                    if (!string.IsNullOrEmpty(anchor)) { dir = Path.GetDirectoryName(anchor); bn = Path.GetFileNameWithoutExtension(anchor)?.Replace("-mix", "").Replace("-system", "").Replace("-mic", ""); }
                }
                catch { }
                if (!string.IsNullOrEmpty(dir) && !string.IsNullOrEmpty(bn))
                {
                    string? firstTarget = _faults.FirstError != null ? _faults.FirstTarget.ToString() : null;
                    SessionManifest.MarkClosed(dir, bn, TotalMixBytesWritten,
                        _faults.DiskFullLatched || _diskFullStopRequested,
                        _faults.TotalErrors, _faults.SysErrors, _faults.MicErrors, _faults.MixErrors,
                        firstTarget, _faults.FirstError?.Message);
                }
            }
            catch { }

            CloseDumpStreams();
            NativeTiming.End1Ms();
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

        public void StopMonitor() => StopInternal(fullStop: true);

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            lock (_devLock) { _reopenCts?.Cancel(); _loopLost = _micLost = false; }
            FinalizeWavFiles();
            ReleaseCaptureMmcss();
            NativeTiming.End1Ms();

            // Stop the write queue if it's still running. 
            // We check IsAddingCompleted to avoid InvalidOperationException if StopAsync was already called.
            try
            {
                if (_writeQueue != null && !_writeQueue.IsAddingCompleted)
                {
                    _writeQueue.CompleteAdding();
                }
                _writeTask?.Wait(1000);
            }
            catch { }
            try { _writeQueue?.Dispose(); } catch { }

            StopInternal(fullStop: true);
            CloseDumpStreams();
            TryDispose(ref _log);
        }

        private void OpenDumpStreams(string dumpDir, string outDir, string baseName)
        {
            CloseDumpStreams();
            if (string.IsNullOrWhiteSpace(dumpDir)) return;
            try
            {
                Directory.CreateDirectory(dumpDir);
                _dumpMic = new FileStream(Path.Combine(dumpDir, $"{baseName}-microphoneIn.pcm"), FileMode.Create, FileAccess.Write, FileShare.Read);
                _dumpSys = new FileStream(Path.Combine(dumpDir, $"{baseName}-speakerIn.pcm"), FileMode.Create, FileAccess.Write, FileShare.Read);
                _dumpMix = new FileStream(Path.Combine(dumpDir, $"{baseName}-mixOut.pcm"), FileMode.Create, FileAccess.Write, FileShare.Read);
                Info($"Dump dir: {dumpDir}");
            }
            catch (Exception ex)
            {
                Warn($"Dump dir unavailable, continuing without dumps: {ex.Message}");
                CloseDumpStreams();
            }
        }

        private void CloseDumpStreams()
        {
            lock (_dumpLock)
            {
                try { _dumpMic?.Dispose(); } catch { }
                try { _dumpSys?.Dispose(); } catch { }
                try { _dumpMix?.Dispose(); } catch { }
                _dumpMic = _dumpSys = _dumpMix = null;
            }
        }

        /// <summary>GAP-06: idempotent WAV finalize — Dispose()s writers so RIFF sizes are
        /// rewritten, safe from StopAsync, Dispose, or abort paths.</summary>
        private (bool okSys, bool okMic, bool okMix) FinalizeWavFiles()
        {
            bool okSys = false, okMic = false, okMix = false;
            try { _wavSys?.Dispose(); okSys = File.Exists(_pathSys) && new FileInfo(_pathSys).Length > 44; } catch { }
            try { _wavMic?.Dispose(); okMic = File.Exists(_pathMic) && new FileInfo(_pathMic).Length > 44; } catch { }
            try { _wavMix?.Dispose(); okMix = File.Exists(_pathMix) && new FileInfo(_pathMix).Length > 44; } catch { }
            _wavSys = null; _wavMic = null; _wavMix = null;
            return (okSys, okMic, okMix);
        }

        private void WriteDump(FileStream? stream, float[] data, int count)
        {
            if (stream == null || count <= 0) return;
            try
            {
                byte[] tmp = new byte[count * 4];
                Buffer.BlockCopy(data, 0, tmp, 0, count * 4);
                lock (_dumpLock) { stream.Write(tmp, 0, tmp.Length); }
            }
            catch { }
        }

        // ==========================================
        // BACKGROUND WRITE LOOP
        // ==========================================
        // This runs on a separate thread. It pulls data chunks from the queue and writes them to disk.
        // This ensures that slow disk I/O never blocks the audio callback methods.
        private void DiskWriteLoop()
        {
            if (_writeQueue == null) return;
            int mmcss = NativeTiming.BoostCurrentThread();

            try
            {
                // GetConsumingEnumerable blocks until an item is available or CompleteAdding is called.
                foreach (var job in _writeQueue.GetConsumingEnumerable())
                {
                    try
                    {
                        switch (job.Target)
                        {
                            case AudioFileTarget.System: _wavSys?.Write(job.Data, 0, job.Count); break;
                            case AudioFileTarget.Mic:    _wavMic?.Write(job.Data, 0, job.Count); break;
                            case AudioFileTarget.Mix:    _wavMix?.Write(job.Data, 0, job.Count); break;
                        }
                    }
                    catch (Exception ex)
                    {
                        // Sticky counters (DealFfmpegMuxError equivalent); ENOSPC latches
                        // an immediate graceful stop — intake halts, queued audio still
                        // drains so FinalizeWavFiles rewrites headers (av_write_trailer-always).
                        bool latched = _faults.Record(job.Target, ex);
                        Warn($"Disk write failed ({job.Target}): {ex.Message} " +
                             $"(n={_faults.TotalErrors}, first={_faults.FirstTarget}:{_faults.FirstError?.Message})");
                        Debug.WriteLine($"Disk write error: {ex.Message}");
                        if (latched && !_diskFullStopRequested)
                        {
                            _diskFullStopRequested = true;
                            _recording = false;
                            RaiseStatus(EngineStatusKind.Error,
                                "Disk full — stopping gracefully. Already-written audio will be finalized; free space, then press Stop.");
                            try { _writeQueue?.CompleteAdding(); } catch { }
                        }
                    }
                    finally
                    {
                        // IMPORTANT: Return the rented buffer to the pool so it can be reused.
                        // This creates a "Zero Allocation" loop for buffer memory.
                        ArrayPool<byte>.Shared.Return(job.Data);
                    }
                }
            }
            catch (Exception ex)
            {
                _writerException = ex;
                _recording = false; // Stop accepting new data if writer fails
                Error("DiskWriteLoop fatal", ex);
                RaiseStatus(EngineStatusKind.Error, $"Disk write failed: {ex.Message}");
            }
            finally
            {
                NativeTiming.Unboost(mmcss);
            }
        }

        private int CalculateOptimalQueueSize()
        {
            // Based on sample rate and block size, calculate ~10 seconds of buffer
            return Math.Max(DefaultQueueCapacity, (_outRate * _outChannels * 10) / (BlockFrames * _outChannels));
        }

        private void EnqueueWrite(AudioFileTarget target, byte[] sourceData, int count)
        {
            if (!_recording || _diskFullStopRequested || _writeQueue == null || _writeQueue.IsAddingCompleted) return;

            byte[] rented = ArrayPool<byte>.Shared.Rent(count);
            bool added = false;
            try
            {
                Array.Copy(sourceData, 0, rented, 0, count);
                added = _writeQueue.TryAdd(new AudioWriteJob(target, rented, count));
            }
            finally
            {
                if (!added) ArrayPool<byte>.Shared.Return(rented);
            }

            if (added && target == AudioFileTarget.Mix)
                Interlocked.Add(ref _totalMixBytesWritten, count);

            if (added)
            {
                var q = _writeQueue;
                if (Volatile.Read(ref _queueHighWaterWarned) == 0
                    && q != null && WriterHealth.ShouldRaiseHighWater(q.Count, q.BoundedCapacity)
                    && Interlocked.CompareExchange(ref _queueHighWaterWarned, 1, 0) == 0)
                    Warn($"Write queue >75% full ({q.Count}/{q.BoundedCapacity}) — disk is falling behind.");
            }

            if (!added)
            {
                long dropped = Interlocked.Increment(ref _droppedBlocks);
                // Log the first drop immediately, then every 100th drop to avoid log spam.
                // The first drop is a critical indicator of disk I/O bottlenecks.
                if (dropped == 1 || dropped % 100 == 0)
                {
                    Warn($"Write queue full, dropped {dropped} block(s) - disk I/O bottleneck!");
                }
            }
        }

        // ==========================================
        // DEVICES
        // ==========================================

        private async Task OpenDevicesAsync(RecorderStartOptions opts)
        {
            // GAP-09: either source may be disabled (null/empty/whitespace id).
            bool wantLoop = !string.IsNullOrWhiteSpace(opts.LoopbackDeviceId);
            bool wantMic  = !string.IsNullOrWhiteSpace(opts.MicDeviceId);
            if (!wantLoop && !wantMic)
                throw new InvalidOperationException("At least one audio source (system or microphone) is required.");

            bool loopMismatch = wantLoop
                ? (_loopCap == null || _loopCap.Device.DeviceID != opts.LoopbackDeviceId)
                : (_loopCap != null);
            bool micMismatch = wantMic
                ? (_micCap == null || _micCap.Device.DeviceID != opts.MicDeviceId)
                : (_micCap != null);

            if (!loopMismatch && !micMismatch) return;

            Info("Opening devices…");

            try { if (_loopIn != null) _loopIn.DataAvailable -= OnLoopbackData; } catch { }
            try { if (_micIn  != null) _micIn.DataAvailable  -= OnMicData; }      catch { }
            TryStopDispose(ref _loopCap);
            TryStopDispose(ref _micCap);
            TryDispose(ref _loopIn);
            TryDispose(ref _micIn);
            TryDispose(ref _loopSrc);
            TryDispose(ref _micSrc);

            int maxRetries = 3;
            int retryDelayMs = 250;

            for (int attempt = 0; attempt < maxRetries; attempt++)
            {
                try
                {
                    long a0 = Stopwatch.GetTimestamp();
                    MMDevice? loopDev = null;
                    MMDevice? micDev = null;
                    using (var mmde = new MMDeviceEnumerator())
                    {
                        if (wantLoop)
                            loopDev = mmde.GetDevice(opts.LoopbackDeviceId!);
                        if (wantMic)
                            micDev = mmde.GetDevice(opts.MicDeviceId!);
                    }
                    CrashLog.LogOnly("AudioEngine",
                        $"t={DateTime.Now:HH:mm:ss.fff} OpenDevices attempt {attempt + 1} GetDevice +{(Stopwatch.GetTimestamp() - a0) * _tickMs:F0}ms loop={loopDev?.FriendlyName ?? "(none)"} mic={micDev?.FriendlyName ?? "(none)"} tid={Environment.CurrentManagedThreadId}");

                    _loopDevName = loopDev?.FriendlyName ?? "(none)";
                    _micDevName  = micDev?.FriendlyName ?? "(none)";

                    if (loopDev != null)
                    {
                        long l0 = Stopwatch.GetTimestamp();
                        _loopCap = new WasapiLoopbackCapture { Device = loopDev };
                        _loopCap.Initialize();
                        CrashLog.LogOnly("AudioEngine",
                            $"t={DateTime.Now:HH:mm:ss.fff} OpenDevices loop Initialize +{(Stopwatch.GetTimestamp() - l0) * _tickMs:F0}ms sr={_loopCap.WaveFormat.SampleRate} ch={_loopCap.WaveFormat.Channels}");
                        _outRate     = _loopCap.WaveFormat.SampleRate;
                        _outChannels = _loopCap.WaveFormat.Channels;

                        _loopIn  = new SoundInSource(_loopCap) { FillWithZeros = true };
                        _loopSrc = _loopIn.ToSampleSource();
                    }
                    else
                    {
                        // GAP-09: system capture disabled — mic drives the clock,
                        // loop ring stays empty, loopSilent is permanently true.
                        _loopCap = null;
                        _loopIn  = null;
                        _loopSrc = null;
                    }

                    if (micDev != null)
                    {
                        long m0 = Stopwatch.GetTimestamp();
                        _micCap = new WasapiCapture { Device = micDev };
                        _micCap.Initialize();
                        CrashLog.LogOnly("AudioEngine",
                            $"t={DateTime.Now:HH:mm:ss.fff} OpenDevices mic Initialize +{(Stopwatch.GetTimestamp() - m0) * _tickMs:F0}ms sr={_micCap.WaveFormat.SampleRate} ch={_micCap.WaveFormat.Channels}");

                        _micIn  = new SoundInSource(_micCap) { FillWithZeros = true };
                        _micSrc = _micIn.ToSampleSource();

                        lock (_micRingLock) { _micR = _micW = _micCount = 0; }
                        _micResamp.Reset(_micSrc.WaveFormat.Channels, _micSrc.WaveFormat.SampleRate, _outRate);

                        if (!wantLoop)
                        {
                            // No loopback clock: derive output format from the mic.
                            _outRate     = _micSrc.WaveFormat.SampleRate;
                            _outChannels = _micSrc.WaveFormat.Channels;
                            _micResamp.Reset(_micSrc.WaveFormat.Channels, _micSrc.WaveFormat.SampleRate, _outRate);
                        }
                    }
                    else if (wantMic)
                    {
                        RaiseStatus(EngineStatusKind.Error, "Microphone device not found/selected.");
                    }

                    // Record which devices are now active so disconnect detection can check them.
                    _activeLoopDeviceId = opts.LoopbackDeviceId ?? "";
                    _activeMicDeviceId  = opts.MicDeviceId ?? "";

                    return;
                }
                catch (CSCore.CoreAudioAPI.CoreAudioAPIException ex)
                {
                    if (ex.HResult != unchecked((int)0x88890004))
                        throw;

                    CrashLog.LogOnly("AudioEngine",
                        $"t={DateTime.Now:HH:mm:ss.fff} OpenDevices attempt {attempt + 1} device-invalidated (0x{ex.HResult:X8}): {ex.Message}");
                    // Cleanup failed attempt before retry
                    TryStopDispose(ref _loopCap);
                    TryStopDispose(ref _micCap);

                    if (attempt < maxRetries - 1)
                    {
                        Info($"Device invalidated, retrying in {retryDelayMs}ms...");
                        await Task.Delay(retryDelayMs);
                    }
                    else
                    {
                        throw;
                    }
                }
            }
        }

        private void StopInternal(bool fullStop)
        {
            long t0 = Stopwatch.GetTimestamp();
            bool wasMonitoring = _monitoring;
            if (_monitoring)
            {
                try { if (_loopIn != null) _loopIn.DataAvailable -= OnLoopbackData; } catch { }
                try { if (_micIn != null) _micIn.DataAvailable -= OnMicData; } catch { }

                TryDispose(ref _loopIn);
                TryDispose(ref _micIn);
                TryDispose(ref _loopSrc);
                TryDispose(ref _micSrc);

                TryStopDispose(ref _loopCap);
                TryStopDispose(ref _micCap);
                _monitoring = false;

                // Clear tracked device IDs when monitoring stops.
                _activeLoopDeviceId = "";
                _activeMicDeviceId  = "";
            }

            ReleaseCaptureMmcss();

            if (wasMonitoring)
                CrashLog.LogOnly("AudioEngine",
                    $"t={DateTime.Now:HH:mm:ss.fff} StopInternal fullStop={fullStop} +{(Stopwatch.GetTimestamp() - t0) * _tickMs:F0}ms");
            if (fullStop) Info("Stopped.");
        }

        /// <summary>
        /// Called by the UI when Windows reports a device removal or state change to Unplugged.
        /// While recording the file stays open: the lost source is detached, the survivor
        /// keeps feeding the mix via silence-bridge, and a backoff loop re-opens the device
        /// into the same file (frozen format). Monitoring-only still tears down.
        /// Safe to call from any thread.
        /// </summary>
        public async Task HandleDeviceDisconnectedAsync(string deviceId)
        {
            if (!_monitoring) return;
            if (deviceId != _activeLoopDeviceId && deviceId != _activeMicDeviceId) return;

            if (_recording)
            {
                bool isLoop = deviceId == _activeLoopDeviceId;
                CancellationToken token;
                lock (_devLock)
                {
                    if (isLoop) _loopLost = true; else _micLost = true;
                    _reopenCts?.Cancel();
                    _reopenCts = new CancellationTokenSource();
                    token = _reopenCts.Token;
                }
                DetachSource(isLoop);
                RaiseStatus(EngineStatusKind.Error,
                    $"Audio device disconnected — continuing same file on silence, retrying {(isLoop ? "system" : "mic")}…");
                Info($"Device lost ({(isLoop ? "loop" : "mic")} id={deviceId}); surviving source continues.");
                await Task.Yield();
                _ = ReopenLostSourceLoopAsync(isLoop, token);
            }
            else
            {
                StopInternal(fullStop: true);
                RaiseStatus(EngineStatusKind.Info, "Audio device disconnected.");
            }
        }

        /// <summary>Single-shot resume attempt for OnDeviceAdded; no-op when nothing is lost.</summary>
        public Task TryResumeLostSourcesAsync()
        {
            CancellationToken token;
            lock (_devLock)
            {
                _reopenCts?.Cancel();
                _reopenCts = new CancellationTokenSource();
                token = _reopenCts.Token;
            }
            return Task.Run(async () =>
            {
                await ReopenLostSourceLoopAsync(isLoop: true, token, resumeOnly: true);
                await ReopenLostSourceLoopAsync(isLoop: false, token, resumeOnly: true);
            });
        }

        private void DetachSource(bool isLoop)
        {
            try { if (isLoop && _loopIn != null) _loopIn.DataAvailable -= OnLoopbackData; } catch { }
            try { if (!isLoop && _micIn != null) _micIn.DataAvailable -= OnMicData; } catch { }
            if (isLoop) { TryStopDispose(ref _loopCap); TryDispose(ref _loopIn); TryDispose(ref _loopSrc); }
            else { TryStopDispose(ref _micCap); TryDispose(ref _micIn); TryDispose(ref _micSrc); }
            if (isLoop) lock (_loopRingLock) { _loopR = _loopW = _loopCount = 0; }
            if (!isLoop) lock (_micRingLock) { _micR = _micW = _micCount = 0; }
            _micResamp.Reset(-1, 0, 0);
            try { Array.Clear(_resampleScratch, 0, _resampleScratch.Length); } catch { }
        }

        private async Task ReopenLostSourceLoopAsync(bool isLoop, CancellationToken token, bool resumeOnly = false)
        {
            int attempt = 0;
            while (true)
            {
                lock (_devLock)
                {
                    if (isLoop ? !_loopLost : !_micLost) return;
                    if (!_monitoring || !_recording || _disposed) return;
                }
                if (resumeOnly && attempt > 0) return;
                if (token.IsCancellationRequested) return;
                try
                {
                    string devId = isLoop ? _activeLoopDeviceId : _activeMicDeviceId;
                    if (string.IsNullOrEmpty(devId)) return;
                    MMDevice dev;
                    using (var mmde = new MMDeviceEnumerator()) dev = mmde.GetDevice(devId);
                    if (isLoop)
                    {
                        var cap = new WasapiLoopbackCapture { Device = dev };
                        cap.Initialize();
                        _loopCap = cap;
                        _loopIn = new SoundInSource(_loopCap) { FillWithZeros = true };
                        _loopSrc = _loopIn.ToSampleSource();
                        _loopIn.DataAvailable += OnLoopbackData;
                        _loopCap.Start();
                        _lastLoopTick = Stopwatch.GetTimestamp();
                        lock (_loopRingLock) { _loopR = _loopW = _loopCount = 0; }
                    }
                    else
                    {
                        var cap = new WasapiCapture { Device = dev };
                        cap.Initialize();
                        _micCap = cap;
                        _micIn = new SoundInSource(_micCap) { FillWithZeros = true };
                        _micSrc = _micIn.ToSampleSource();
                        lock (_micRingLock) { _micR = _micW = _micCount = 0; }
                        try { Array.Clear(_resampleScratch, 0, _resampleScratch.Length); } catch { }
                        _micIn.DataAvailable += OnMicData;
                        _micCap.Start();
                    }
                    lock (_devLock) { if (isLoop) _loopLost = false; else _micLost = false; }
                    Info($"Re-opened {(isLoop ? "loopback" : "mic")} on same file: {dev?.FriendlyName}");
                    RaiseStatus(EngineStatusKind.Info, $"Audio device reconnected — resumed same file ({(isLoop ? "system" : "mic")}).");
                    return;
                }
                catch (Exception ex) when (ex is CoreAudioAPIException || ex is System.Runtime.InteropServices.COMException || ex is FileNotFoundException || ex is DirectoryNotFoundException)
                {
                    int delay = attempt switch { 0 => 500, 1 => 1000, 2 => 2000, _ => 5000 };
                    if (attempt % 4 == 0) Info($"Re-open {(isLoop ? "loop" : "mic")} attempt {attempt + 1} failed, retry in {delay}ms: {ex.Message}");
                    attempt++;
                    try { await Task.Delay(delay, token); } catch (OperationCanceledException) { return; }
                }
            }
        }

        // ==========================================
        // AUDIO CALLBACKS
        // ==========================================

        private void OnMicData(object? sender, DataAvailableEventArgs e)
        {
            EnsureCaptureBoost();
            if (_micSrc == null || _micIn == null || _micCap == null) return;
            RecordCallbackTiming(isLoop: false);
            long mrtMic = Volatile.Read(ref _restartTick);
            if (mrtMic != 0 && Interlocked.Exchange(ref _restartLogged, 1) == 0)
                CrashLog.LogOnly("AudioEngine",
                    $"t={DateTime.Now:HH:mm:ss.fff} first post-restart mic data after {(Stopwatch.GetTimestamp() - mrtMic) * _tickMs:F0}ms tid={Environment.CurrentManagedThreadId}");
            try
            {
                int blockAlignBytes = _micIn.WaveFormat.BlockAlign; 
                if (blockAlignBytes <= 0) return;

                int framesAvail = e.ByteCount / blockAlignBytes;
                if (framesAvail <= 0) return;

                int floatSamplesToRead = framesAvail * _micSrc.WaveFormat.Channels;
                EnsureCapacity(ref _micInBuf, floatSamplesToRead);

                int got = ReadExactSamples(_micSrc, _micInBuf, floatSamplesToRead);
                if (got <= 0) return;

                // Optimization: Pass scratch buffer to avoid allocations inside ConvertToTarget
                int conv = ConvertToTargetChunked(_micInBuf, got, _micSrc.WaveFormat, _outRate, _outChannels, ref _micConvBuf, ref _resampleScratch, _micResamp);
                WriteDump(_dumpMic, _micConvBuf, conv);

                long nowTicksA = Stopwatch.GetTimestamp();
                double msSinceLoop = (nowTicksA - _lastLoopTick) * _tickMs;
                // GAP-09: no loopback device → mic permanently drives the mix.
                bool loopSilent = _loopCap == null || msSinceLoop > LoopSilentMsThreshold;

                // --- Metering (post-mute: muted channel reads live 0) ---
                float blockPeak = 0f;
                double blockRmsSum = 0.0;
                bool blockClipped = false;
                double micGainSnap = MicGain;
                bool micMutedSnap = MicMuted;
                for (int i = 0; i < conv; i++)
                {
                    float sample = micMutedSnap ? 0f : (float)(_micConvBuf[i] * micGainSnap);
                    float abs = MathF.Abs(sample);
                    if (abs > blockPeak) blockPeak = abs;
                    blockRmsSum += (double)abs * abs;
                    if (abs > 1f) blockClipped = true;
                }
                if (blockPeak > _peakSinceLastMic) _peakSinceLastMic = blockPeak;
                _rmsSumSinceLastMic += blockRmsSum;
                _rmsCountSinceLastMic += conv;
                _clippedSinceLastMic |= blockClipped;

                double msSince = (nowTicksA - _lastLevelTickMic) * _tickMs;
                if (msSince >= LevelThrottleMs && _rmsCountSinceLastMic > 0)
                {
                    if (_paused)
                    {
                        _peakSinceLastMic = 0f;
                        _rmsSumSinceLastMic = 0.0;
                        _rmsCountSinceLastMic = 0;
                        _clippedSinceLastMic = false;
                        _lastLevelTickMic = nowTicksA;
                    }
                    else
                    {
                    float rms = (float)Math.Sqrt(_rmsSumSinceLastMic / _rmsCountSinceLastMic);
                    LevelChanged?.Invoke(this, new LevelChangedEventArgs(LevelSource.Mic, rms, _peakSinceLastMic, _clippedSinceLastMic));
                    _peakSinceLastMic = 0f;
                    _rmsSumSinceLastMic = 0.0;
                    _rmsCountSinceLastMic = 0;
                    _clippedSinceLastMic = false;
                    _lastLevelTickMic = nowTicksA;
                    }
                }

                // --- Sync Logic: always buffer mic; fixed-frame drain emits. ---
                {
                    int overruns = 0;
                    long capDropped;
                    lock (_micRingLock)
                    {
                        capDropped = EnsureRingCapacity(conv);
                        for (int i = 0; i < conv; i++)
                        {
                            _micRing[_micW] = _micConvBuf[i];
                            _micW = (_micW + 1) % _micRing.Length;
                            if (_micCount < _micRing.Length) _micCount++;
                            else { _micR = (_micR + 1) % _micRing.Length; overruns++; }
                        }
                    }
                    long totalCap = capDropped;
                    if (capDropped > 0)
                    {
                        totalCap = Interlocked.Read(ref _micOverrunBlocks);
                        if (totalCap <= 10 || totalCap % 48000 == 0)
                            Warn($"Mic ring overrun: dropped {capDropped} samples (total {totalCap}) - mic faster than loopback");
                    }
                    if (overruns > 0)
                    {
                        long total = Interlocked.Add(ref _micOverrunBlocks, overruns);
                        if (total <= 10 || total % 48000 == 0)
                            Warn($"Mic ring full: dropped {overruns} oldest samples (total {total})");
                    }
                }

                // --- Write Mic WAV (Async) ---
                if (_recording && _writeStems && _wavMic != null)
                {
                    if (_paused) Array.Clear(_micConvBuf, 0, conv);
                    EnsureCapacity(ref _pcm16Mic, conv * 2);
                    FloatToPcm16(_micConvBuf, _pcm16Mic, conv); 
                    // Queue for background writing instead of blocking here
                    EnqueueWrite(AudioFileTarget.Mic, _pcm16Mic, conv * 2);
                }

                // --- Mic-Only Drive (if loopback silent): fixed-frame emit ---
                if (_recording && _wavMix != null && loopSilent)
                {
                    int n = FixedFrameFloats();
                    EnsureCapacity(ref _frameMic, n);
                    while (true)
                    {
                        lock (_micRingLock)
                        {
                            if (_micCount < n) break;
                            for (int i = 0; i < n; i++) { _frameMic[i] = _micRing[_micR]; _micR = (_micR + 1) % _micRing.Length; }
                            _micCount -= n;
                        }
                        if (_preRollRemaining > 0) { _preRollRemaining--; Array.Clear(_frameMic, 0, n); }
                        if (_paused) Array.Clear(_frameMic, 0, n);
                        Interlocked.Add(ref _masterFramesEmitted, FixedFramesPerChannel);
                        EmitMicOnlyFixed(_frameMic, n);
                    }
                }
            }
            catch (CSCore.CoreAudioAPI.CoreAudioAPIException ex) when (ex.HResult == unchecked((int)0x88890004))
            {
                // GAP-06: keep file open; loopback clock zero-fills this source.
                _ = HandleDeviceDisconnectedAsync(_activeMicDeviceId);
            }
            catch (Exception ex) { Error("OnMicData", ex); }
        }

        private void OnLoopbackData(object? sender, DataAvailableEventArgs e)
        {
            EnsureCaptureBoost();
            if (_loopSrc == null || _loopCap == null || _loopIn == null) return;
            RecordCallbackTiming(isLoop: true);
            long mrtLoop = Volatile.Read(ref _restartTick);
            if (mrtLoop != 0 && Interlocked.Exchange(ref _restartLogged, 1) == 0)
                CrashLog.LogOnly("AudioEngine",
                    $"t={DateTime.Now:HH:mm:ss.fff} first post-restart loopback data after {(Stopwatch.GetTimestamp() - mrtLoop) * _tickMs:F0}ms tid={Environment.CurrentManagedThreadId}");

            try
            {

                int bytesPerFrame = _loopIn.WaveFormat.BlockAlign; 
                if (bytesPerFrame <= 0) return;

                int frames = e.ByteCount / bytesPerFrame;
                if (frames <= 0) return;

                int wantFloats = frames * _outChannels;
                EnsureCapacity(ref _loopBufF, wantFloats);

                int gotLoop = ReadExactSamples(_loopSrc, _loopBufF, wantFloats);
                if (gotLoop <= 0) return;

                // --- Metering (post-mute: muted channel reads live 0) ---
                float blockPeak = 0f;
                double blockRmsSum = 0.0;
                bool blockClipped = false;
                double loopGainSnap = LoopGain;
                bool sysMutedSnap = SystemMuted;
                for (int i = 0; i < gotLoop; i++)
                {
                    float sample = sysMutedSnap ? 0f : (float)(_loopBufF[i] * loopGainSnap);
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
                    if (_paused)
                    {
                        _peakSinceLastSys = 0f;
                        _rmsSumSinceLastSys = 0.0;
                        _rmsCountSinceLastSys = 0;
                        _clippedSinceLastSys = false;
                        _lastLevelTickSys = nowTicks;
                    }
                    else
                    {
                    float rms = (float)Math.Sqrt(_rmsSumSinceLastSys / _rmsCountSinceLastSys);
                    LevelChanged?.Invoke(this, new LevelChangedEventArgs(LevelSource.System, rms, _peakSinceLastSys, _clippedSinceLastSys));
                    _peakSinceLastSys = 0f;
                    _rmsSumSinceLastSys = 0.0;
                    _rmsCountSinceLastSys = 0;
                    _clippedSinceLastSys = false;
                    _lastLevelTickSys = nowTicks;
                    }
                }

                // --- Write System WAV (Async) ---
                if (_recording && _writeStems && _wavSys != null)
                {
                    if (_paused) Array.Clear(_loopBufF, 0, gotLoop);
                    EnsureCapacity(ref _pcm16Sys, gotLoop * 2);
                    FloatToPcm16(_loopBufF, _pcm16Sys, gotLoop);
                    EnqueueWrite(AudioFileTarget.System, _pcm16Sys, gotLoop * 2);
                }
                WriteDump(_dumpSys, _loopBufF, gotLoop);

                // --- Dual-FIFO tick: buffer loopback, emit fixed frames. ---
                long loopDropped = PushLoopRing(_loopBufF, gotLoop);
                if (loopDropped > 0)
                {
                    long totalLoop = Interlocked.Read(ref _loopOverrunBlocks);
                    if (totalLoop <= 10 || totalLoop % 48000 == 0)
                        Warn($"Loop ring overrun: dropped {loopDropped} samples (total {totalLoop})");
                }

                // --- Diagnostics + continuous drift trim + master-clock resync ---
                double backlogSec = MicBacklogSec();
                if (!_paused) backlogSec = TryGentleTrim(backlogSec);
                int snapshotMicCount;
                lock (_micRingLock) snapshotMicCount = _micCount;
                _lastMicBacklogSec = backlogSec;
                if (backlogSec > _micBacklogSecMax) _micBacklogSecMax = backlogSec;
                if (!_paused) CheckLagResync(backlogSec);

                // --- Fixed-frame emit (480 frames = 960 floats stereo) ---
                int fixedN = FixedFrameFloats();
                EnsureCapacity(ref _frameLoop, fixedN);
                EnsureCapacity(ref _frameMic, fixedN);
                while (TryPopFixedFrame(_frameLoop, _frameMic))
                {
                    if (_paused) { Array.Clear(_frameLoop, 0, fixedN); Array.Clear(_frameMic, 0, fixedN); }
                    EmitFixedMix(_frameLoop, _frameMic, fixedN);
                }

                _loopBlockCounter++;
                // lagTime-equivalent: wall-clock 1 Hz line (RecExperts style), not callback-count anchored.
                // Includes FIFO depth, over/under counters, backlog max + master clock + resync count.
                long nowLagTick = Stopwatch.GetTimestamp();
                if (_lastLagLogTick == 0) _lastLagLogTick = nowLagTick;
                double secSinceLag = (nowLagTick - _lastLagLogTick) * _tickMs / 1000.0;
                if (secSinceLag >= 1.0)
                {
                    _lastLagLogTick = nowLagTick;
                    long over = Interlocked.Read(ref _micOverrunBlocks);
                    long under = Interlocked.Read(ref _micUnderrunBlocks);
                    long loopOver = Interlocked.Read(ref _loopOverrunBlocks);
                    long trimD = Interlocked.Exchange(ref _trimTickDropped, 0);
                    long trimI = Interlocked.Exchange(ref _trimTickInserted, 0);
                    long trimDTot = Interlocked.Read(ref _trimTotalDropped);
                    long trimITot = Interlocked.Read(ref _trimTotalInserted);
                    Info($"lagTime={backlogSec:F3}s micDepth={snapshotMicCount} over={over} loopOver={loopOver} under={under} silenced={_silencedSamples} backlogMax={_micBacklogSecMax:F3}s masterFrames={_masterFramesEmitted} resyncs={_resyncCount} trim={trimD}/{trimI}/s trimTot={trimDTot}/{trimITot} lagWatch={(backlogSec > 0.08 ? "LAG" : "ok")}");
                }
                else if (_loopBlockCounter % BacklogLogEveryNBlocks == 0)
                {
                    Info($"Mic ring backlog: {backlogSec:F4} s (max {_micBacklogSecMax:F4} s)");
                }
            }
            catch (CSCore.CoreAudioAPI.CoreAudioAPIException ex) when (ex.HResult == unchecked((int)0x88890004))
            {
                // GAP-06: keep file open; mic-only drive takes over the mix.
                _ = HandleDeviceDisconnectedAsync(_activeLoopDeviceId);
            }
            catch (Exception ex) { Error("OnLoopbackData", ex); }
        }

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

        // ==========================================
        // DSP & CONVERSION
        // ==========================================

        // Modified: Now takes 'ref scratch' to avoid "new float[]" allocations in the loop.
        // Single-shot wrapper (tests/compat); hot path uses ConvertToTargetChunked with carried phase.
        private static int ConvertToTarget(
            float[] src, int floatCount, CSCore.WaveFormat srcFmt,
            int dstRate, int dstCh,
            ref float[] dst, ref float[] scratch)
        {
            var st = new ResamplerState();
            st.Reset(srcFmt.Channels, srcFmt.SampleRate, dstRate);
            return ConvertToTargetChunked(src, floatCount, srcFmt, dstRate, dstCh, ref dst, ref scratch, st);
        }

        private static void GetFold(float[] temp, int si, int srcCh, out float L, out float R, out float C, out float SL, out float SR)
        {
            // WAVEFORMATEXTENSIBLE canonical order: L,R,C,LFE,SL,SR. LFE deliberately excluded
            // from folds (prevents bass boom; downstream peak-normalize absorbs full-scale correlation).
            L = temp[si]; R = srcCh > 1 ? temp[si + 1] : L; C = 0; SL = 0; SR = 0;
            if (srcCh == 4) { SL = temp[si + 2]; SR = temp[si + 3]; }
            else if (srcCh >= 6) { C = temp[si + 2]; SL = temp[si + 4]; SR = temp[si + 5]; }
            else if (srcCh >= 3) { C = temp[si + 2]; if (srcCh >= 5) { SL = temp[si + 3]; SR = temp[si + 4]; } }
        }

        private static int ConvertToTargetChunked(
            float[] src, int floatCount, CSCore.WaveFormat srcFmt,
            int dstRate, int dstCh,
            ref float[] dst, ref float[] scratch, ResamplerState st)
        {
            int srcCh = srcFmt.Channels;
            int srcRate = srcFmt.SampleRate;

            int srcFrames = floatCount / srcCh;
            if (srcFrames <= 0) return 0;
            if (st.PrevCh != srcCh || st.SrcRate != srcRate || st.DstRate != dstRate)
                st.Reset(srcCh, srcRate, dstRate);

            int dstFrames;
            if (srcRate == dstRate)
            {
                dstFrames = srcFrames;
            }
            else
            {
                dstFrames = (int)Math.Floor((srcFrames - st.NextSrcPos) * (double)dstRate / srcRate);
                if (dstFrames < 0) dstFrames = 0;
            }

            EnsureCapacity(ref dst, Math.Max(dstCh, srcCh) * Math.Max(1, dstFrames));

            float[] temp = src;
            int tempFrames = srcFrames;

            if (srcRate != dstRate)
            {
                EnsureCapacity(ref scratch, Math.Max(1, srcCh * dstFrames));
                temp = scratch;

                const int Taps = SincPolyphase.Taps;
                const int Half = SincPolyphase.Half;
                const int Phases = SincPolyphase.Phases;
                var table = SincPolyphase.Table; // local alias, no per-tap static lookup

                double ratio = (double)srcRate / dstRate;
                int di = 0;
                for (int f = 0; f < dstFrames; f++)
                {
                    // Linear-phase kernel centred Half src samples in the past:
                    // every tap is causal in [history|chunk], so chunked and
                    // single-shot read identical sample values (bit-identical).
                    double cp = st.NextSrcPos + f * ratio - Half;
                    int i0 = (int)Math.Floor(cp);
                    double frac = cp - i0;
                    int ph = (int)(frac * Phases);
                    if (ph < 0) ph = 0; else if (ph >= Phases) ph = Phases - 1;
                    int baseIdx = i0 - Half + 1;
                    for (int c = 0; c < srcCh; c++)
                    {
                        double acc = 0.0;
                        for (int k = 0; k < Taps; k++)
                        {
                            int idx = baseIdx + k;
                            float s;
                            if (idx >= 0)
                                s = idx < srcFrames ? src[idx * srcCh + c] : src[(srcFrames - 1) * srcCh + c];
                            else
                            {
                                int h = st.PrevFrames + idx;
                                s = h >= 0 ? st.PrevTail[h * srcCh + c] : 0f;
                            }
                            acc += s * table[ph, k];
                        }
                        temp[di++] = (float)acc;
                    }
                }
                tempFrames = dstFrames;
                st.NextSrcPos = (st.NextSrcPos + dstFrames * ratio) - srcFrames;
                // Carry the last HistFrames source frames (merge-shift handles
                // chunks shorter than the history window). Zero heap allocs on
                // the steady-state path unless the channel count grows.
                int needTail = SincPolyphase.HistFrames * srcCh;
                if (st.PrevTail.Length < needTail) Array.Resize(ref st.PrevTail, needTail);
                int copyFrames = Math.Min(srcFrames, SincPolyphase.HistFrames);
                int keepFrames = Math.Min(st.PrevFrames, SincPolyphase.HistFrames - copyFrames);
                if (keepFrames > 0)
                    Array.Copy(st.PrevTail, (st.PrevFrames - keepFrames) * srcCh, st.PrevTail, 0, keepFrames * srcCh);
                Array.Copy(src, (srcFrames - copyFrames) * srcCh, st.PrevTail, keepFrames * srcCh, copyFrames * srcCh);
                st.PrevFrames = keepFrames + copyFrames;
                st.PrevCh = srcCh;
                st.TotalSrcFrames += srcFrames;
                st.TotalDstFrames += dstFrames;
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
            else if (dstCh == 1)
            {
                if (srcCh == 2)
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
                        GetFold(temp, f * srcCh, srcCh, out float L, out float R, out float C, out float SL, out float SR);
                        float lo = L + 0.7071f * C + 0.5f * SL;
                        float ro = R + 0.7071f * C + 0.5f * SR;
                        dst[di2++] = 0.5f * (lo + ro);
                    }
                }
            }
            else if (dstCh == 2)
            {
                for (int f = 0; f < tempFrames; f++)
                {
                    GetFold(temp, f * srcCh, srcCh, out float L, out float R, out float C, out float SL, out float SR);
                    dst[di2++] = L + 0.7071f * C + 0.5f * SL;
                    dst[di2++] = R + 0.7071f * C + 0.5f * SR;
                }
            }
            else
            {
                // dstCh>2: passthrough overlap + zero-fill, never duplicate into C/LFE.
                for (int f = 0; f < tempFrames; f++)
                {
                    int si = f * srcCh;
                    if (srcCh == 1)
                    {
                        float m = temp[si];
                        for (int c = 0; c < dstCh; c++)
                            dst[di2++] = c < 2 ? m : 0f;
                    }
                    else
                    {
                        int copy = Math.Min(srcCh, dstCh);
                        for (int c = 0; c < copy; c++) dst[di2++] = temp[si + c];
                        for (int c = copy; c < dstCh; c++) dst[di2++] = 0f;
                    }
                }
            }

            return di2;
        }

        private static float ComputeMixScale(float peak)
        {
            // amix-style: normalize only when overshooting 0 dBFS, otherwise bit-transparent.
            return peak > 1f ? 1f / peak : 1f;
        }

        private static float SoftClipIfNeeded(float x)
        {
            // Conditional limiter: passthrough inside [-1,1] so quiet passages
            // stay untouched; tanh soft-knee only on true overs (post-normalize
            // this is a safety net, e.g. NaN/Inf or rounding slightly over 1).
            if (float.IsNaN(x)) return 0f;
            float abs = MathF.Abs(x);
            if (abs <= 1f) return x;
            return MathF.Sign(x) * MathF.Tanh(abs);
        }

        private static readonly ThreadLocal<Random> _rng =
            new ThreadLocal<Random>(() => new Random(
                HashCode.Combine(
                    Environment.TickCount64,
                    Environment.CurrentManagedThreadId,
                    Guid.NewGuid().GetHashCode())));

        private static void FloatToPcm16(float[] src, byte[] dst, int count)
        {
            int j = 0;
            var rng = _rng.Value!;
            for (int i = 0; i < count; i++)
            {
                float v = src[i];
                if (v > 1f) v = 1f; else if (v < -1f) v = -1f;

                float scaled = v * 32767.0f;
                float dither = (float)rng.NextDouble() - (float)rng.NextDouble();
                float withDither = scaled + dither; 

                int s = (int)MathF.Round(withDither);
                if (s > short.MaxValue) s = short.MaxValue;
                else if (s < short.MinValue) s = short.MinValue;

                dst[j++] = (byte)(s & 0xFF);
                dst[j++] = (byte)((s >> 8) & 0xFF);
            }
        }

        /// <summary>
        /// Converts float samples to 32-bit PCM bytes.
        /// Uses long for intermediate calculation to avoid overflow on loud signals.
        /// </summary>
        private static void FloatToPcm32(float[] src, byte[] dst, int count)
        {
            int j = 0;
            for (int i = 0; i < count; i++)
            {
                float v = src[i];
                if (v > 1f) v = 1f; else if (v < -1f) v = -1f;
                
                // Use long for intermediate calculation to avoid overflow.
                // Asymmetric scale: +1.0 → +int.MaxValue, -1.0 → int.MinValue (WAVE spec).
                long scaled = v >= 0
                    ? (long)Math.Round(v * 2147483647.0)
                    : (long)Math.Round(v * 2147483648.0);
                scaled = Math.Clamp(scaled, int.MinValue, int.MaxValue);
                int s = (int)scaled;
                
                dst[j++] = (byte)(s & 0xFF);
                dst[j++] = (byte)((s >> 8) & 0xFF);
                dst[j++] = (byte)((s >> 16) & 0xFF);
                dst[j++] = (byte)((s >> 24) & 0xFF);
            }
        }

        // GAP-01 fixed-frame dual-FIFO tick (reflection-testable).
        // Loopback is the master clock: both sources are buffered, mix emits
        // FixedFramesPerChannel quanta (480 frames = 960 floats stereo).
        private int FixedFrameFloats() { int n = FixedFramesPerChannel * Math.Max(1, _outChannels); Debug.Assert(n == FixedFramesPerChannel * Math.Max(1, _outChannels) && n % Math.Max(1, _outChannels) == 0, "FixedFrameFloats must equal FixedFramesPerChannel*channels and be channel-aligned"); return n; }

        private double MicBacklogSec()
        {
            int c;
            lock (_micRingLock) c = _micCount;
            return (double)c / (Math.Max(1, _outChannels) * Math.Max(1, _outRate)) + MicResamplerDelaySec;
        }

        // Caller holds _loopRingLock; no logging inside (indices only).
        private long EnsureLoopRingCapacity(int roomNeeded)
        {
            if (_loopRing.Length - _loopCount >= roomNeeded) return 0;
            int cap = Math.Max(_loopRing.Length, _outRate * _outChannels * 2);
            int want = _loopCount + roomNeeded;
            if (want <= cap)
            {
                int newLen = Math.Min(cap, NextPow2(want));
                if (newLen <= _loopRing.Length) return 0;
                var newBuf = new float[newLen];
                for (int i = 0; i < _loopCount; i++)
                    newBuf[i] = _loopRing[(_loopR + i) % _loopRing.Length];
                _loopRing = newBuf;
                _loopR = 0;
                _loopW = _loopCount;
                return 0;
            }
            int drop = Math.Min(want - cap, _loopCount);
            if (drop > 0) { _loopR = (_loopR + drop) % _loopRing.Length; _loopCount -= drop; }
            long dropped = want - cap;
            Interlocked.Add(ref _loopOverrunBlocks, dropped);
            return dropped;
        }

        private long PushLoopRing(float[] src, int count)
        {
            long dropped;
            lock (_loopRingLock)
            {
                dropped = EnsureLoopRingCapacity(count);
                for (int i = 0; i < count; i++)
                {
                    _loopRing[_loopW] = src[i];
                    _loopW = (_loopW + 1) % _loopRing.Length;
                    if (_loopCount < _loopRing.Length) _loopCount++;
                    else { _loopR = (_loopR + 1) % _loopRing.Length; Interlocked.Increment(ref _loopOverrunBlocks); }
                }
            }
            return dropped;
        }

        // Pops one fixed frame: full loop quantum + best-effort mic (zero-fill).
        // Advances the master clock. Returns false when loop FIFO is short.
        private bool TryPopFixedFrame(float[] loopDst, float[] micDst)
        {
            int n = FixedFrameFloats();
            if (loopDst == null || micDst == null || loopDst.Length < n || micDst.Length < n) return false;
            Debug.Assert(loopDst.Length >= n && micDst.Length >= n, "TryPopFixedFrame dst buffers must hold one fixed frame (n floats)");
            lock (_loopRingLock)
            {
                if (_loopCount < n) return false;
                for (int i = 0; i < n; i++) { loopDst[i] = _loopRing[_loopR]; _loopR = (_loopR + 1) % _loopRing.Length; }
                _loopCount -= n;
            }
            int micRead;
            lock (_micRingLock)
            {
                micRead = Math.Min(n, _micCount);
                for (int i = 0; i < micRead; i++) { micDst[i] = _micRing[_micR]; _micR = (_micR + 1) % _micRing.Length; }
                _micCount -= micRead;
            }
            if (micRead < n)
            {
                Array.Clear(micDst, micRead, n - micRead);
                if (_preRollRemaining > 0) { _preRollRemaining--; Array.Clear(micDst, 0, n); }
                else { _micUnderrunBlocks++; Interlocked.Add(ref _silencedSamples, n - micRead); }
            }
            else if (_preRollRemaining > 0) { _preRollRemaining--; }
            Interlocked.Add(ref _masterFramesEmitted, FixedFramesPerChannel);
            return true;
        }

        // Master-clock resync: sustained backlog >80 ms drops mic excess back to
        // one fixed frame. Returns true on the tick that resyncs.
        private bool CheckLagResync(double backlogSec)
        {
            Debug.Assert(ResyncThresholdSec == 0.08, "ResyncThresholdSec 80ms contract with watchdog tests");
            if (backlogSec > ResyncThresholdSec) _lagStreak++;
            else _lagStreak = 0;
            if (_lagStreak < ResyncStreakNeeded) return false;
            _lagStreak = 0;
            int keep = FixedFrameFloats();
            int dropped = 0;
            lock (_micRingLock)
            {
                if (_micCount > keep) { dropped = _micCount - keep; _micR = (_micR + dropped) % _micRing.Length; _micCount = keep; }
            }
            if (dropped > 0)
            {
                _resyncCount++;
                Warn($"Master-clock resync: dropped {dropped} mic samples (backlog {backlogSec:F3}s > {ResyncThresholdSec:F2}s, resync #{_resyncCount})");
                return true;
            }
            return false;
        }

        private double TryGentleTrim(double backlogSec)
        {
            int ch = Math.Max(1, _outChannels);
            int rate = Math.Max(1, _outRate);
            double targetSec = TrimTargetFrames * FixedFramesPerChannel / (double)rate + MicResamplerDelaySec;
            double errorSec = backlogSec - targetSec;
            double aerr = errorSec < 0 ? -errorSec : errorSec;
            if (aerr < TrimDeadbandSec) return backlogSec;
            if (aerr >= ResyncThresholdSec) return backlogSec;
            _driftIntegral = _driftIntegral * 0.999 + errorSec;
            if (_driftIntegral > DriftIntegralMax) _driftIntegral = DriftIntegralMax;
            else if (_driftIntegral < -DriftIntegralMax) _driftIntegral = -DriftIntegralMax;
            double corrFloats = errorSec * rate * ch * TrimGainP
                             + _driftIntegral * rate * ch * TrimGainI;
            int want = (int)(corrFloats < 0 ? corrFloats - 0.5 : corrFloats + 0.5);
            int mag = want < 0 ? -want : want;
            if (mag < TrimMinSamples) want = want < 0 ? -TrimMinSamples : TrimMinSamples;
            else if (mag > TrimMaxSamples) want = want < 0 ? -TrimMaxSamples : TrimMaxSamples;
            want -= want % ch;
            if (want == 0) want = errorSec > 0 ? ch : -ch;
            mag = want < 0 ? -want : want;
            if (mag > TrimMaxSamples) want = want < 0 ? -(TrimMaxSamples / ch * ch) : (TrimMaxSamples / ch * ch);
            int dropped = 0, inserted = 0;
            lock (_micRingLock)
            {
                if (want > 0)
                {
                    int drop = want;
                    if (drop > _micCount) drop = (_micCount / ch) * ch;
                    if (drop > 0)
                    {
                        drop = FindZeroCrossDrop(_micRing, _micR, _micCount, drop, ch);
                        if (drop > TrimMaxSamples) drop = (TrimMaxSamples / ch) * ch;
                        _micR = (_micR + drop) % _micRing.Length;
                        _micCount -= drop;
                        dropped = drop;
                    }
                }
                else
                {
                    int ins = -want;
                    EnsureRingCapacity_NoLog_NeedsLock(ins);
                    if (ins > _micRing.Length - _micCount) ins = ((_micRing.Length - _micCount) / ch) * ch;
                    if (ins > 0)
                    {
                        float last = _micCount > 0
                            ? _micRing[(_micW - 1 + _micRing.Length) % _micRing.Length]
                            : 0f;
                        for (int i = 0; i < ins; i++)
                        {
                            float t = (float)(i + 1) / ins;
                            _micRing[_micW] = last * (1f - t);
                            _micW = (_micW + 1) % _micRing.Length;
                            _micCount++;
                        }
                        inserted = ins;
                    }
                }
                backlogSec = (double)_micCount / (ch * rate) + MicResamplerDelaySec;
            }
            if (dropped > 0) { Interlocked.Add(ref _trimTotalDropped, dropped); Interlocked.Add(ref _trimTickDropped, dropped); }
            if (inserted > 0) { Interlocked.Add(ref _trimTotalInserted, inserted); Interlocked.Add(ref _trimTickInserted, inserted); }
            return backlogSec;
        }

        private static int FindZeroCrossDrop(float[] ring, int r, int count, int nominal, int ch)
        {
            int half = TrimZeroCrossWindow / 2;
            int lo = ((nominal - half) / ch) * ch;
            int hi = ((nominal + half) / ch) * ch;
            if (lo < ch) lo = ch;
            if (hi > count) hi = (count / ch) * ch;
            if (hi <= lo) return (nominal / ch) * ch;
            int best = (nominal / ch) * ch;
            int bestQ = int.MaxValue;
            for (int cand = lo; cand <= hi; cand += ch)
            {
                int idx = (r + cand) % ring.Length;
                float a = ring[idx] < 0 ? -ring[idx] : ring[idx];
                float m = a;
                if (ch > 1)
                {
                    float s = ring[(idx + 1) % ring.Length];
                    float b = s < 0 ? -s : s;
                    if (b > m) m = b;
                }
                int q = (int)(m * 32768f);
                if (q < bestQ) { bestQ = q; best = cand; if (q == 0) break; }
            }
            return best;
        }

        private long EnsureRingCapacity_NoLog_NeedsLock(int roomNeeded)
        {
            Debug.Assert(roomNeeded >= 0, "EnsureRingCapacity_NoLog roomNeeded must be >= 0");
            if (_micRing.Length - _micCount >= roomNeeded) { Debug.Assert(_micCount <= _micRing.Length, "micCount must not exceed ring length"); return 0; }
            int cap = Math.Max(_micRing.Length, _outRate * _outChannels * 2);
            int want = _micCount + roomNeeded;
            if (want <= cap)
            {
                int newLen = Math.Min(cap, NextPow2(want));
                if (newLen <= _micRing.Length) return 0;
                var newBuf = new float[newLen];
                for (int i = 0; i < _micCount; i++)
                    newBuf[i] = _micRing[(_micR + i) % _micRing.Length];
                _micRing = newBuf;
                _micR = 0;
                _micW = _micCount;
                Debug.Assert(_micCount <= _micRing.Length, "micCount must not exceed ring length");
                return 0;
            }
            int drop = want - cap;
            drop = Math.Min(drop, _micCount);
            if (drop > 0) { _micR = (_micR + drop) % _micRing.Length; _micCount -= drop; }
            long dropped = want - cap;
            Interlocked.Add(ref _micOverrunBlocks, dropped);
            Debug.Assert(_micCount <= _micRing.Length, "micCount must not exceed ring length");
            return dropped;
        }

        private void EmitFixedMix(float[] loopFrame, float[] micFrame, int n)
        {
            EnsureCapacity(ref _mixBufF, n);
            double loopGain = LoopGain;
            double micGain = MicGain;
            bool sysM = SystemMuted;
            bool micM = MicMuted;
            float peak = 0f;
            for (int i = 0; i < n; i++)
            {
                float s = sysM ? 0f : (float)(loopFrame[i] * loopGain);
                float m = micM ? 0f : (float)(micFrame[i] * micGain);
                float a = s + m;
                _mixBufF[i] = a;
                float abs = MathF.Abs(a);
                if (abs > peak) peak = abs;
            }
            float scale = ComputeMixScale(peak);
            if (scale != 1f) for (int i = 0; i < n; i++) _mixBufF[i] *= scale;
            for (int i = 0; i < n; i++) _mixBufF[i] = SoftClipIfNeeded(_mixBufF[i]);
            WriteDump(_dumpMix, _mixBufF, n);
            if (_recording && _wavMix != null)
            {
                if (_mixUse32Bit) { EnsureCapacity(ref _pcm32Mix, n * 4); FloatToPcm32(_mixBufF, _pcm32Mix, n); EnqueueWrite(AudioFileTarget.Mix, _pcm32Mix, n * 4); }
                else { EnsureCapacity(ref _pcm16Mix, n * 2); FloatToPcm16(_mixBufF, _pcm16Mix, n); EnqueueWrite(AudioFileTarget.Mix, _pcm16Mix, n * 2); }
            }
        }

        private void EmitMicOnlyFixed(float[] micFrame, int n)
        {
            EnsureCapacity(ref _mixBufF, n);
            double micGain = MicGain;
            bool micM = MicMuted;
            float micPeak = 0f;
            for (int i = 0; i < n; i++) { float m = micM ? 0f : (float)(micFrame[i] * micGain); _mixBufF[i] = m; float abs = MathF.Abs(m); if (abs > micPeak) micPeak = abs; }
            float micScale = ComputeMixScale(micPeak);
            for (int i = 0; i < n; i++) _mixBufF[i] = SoftClipIfNeeded(_mixBufF[i] * micScale);
            WriteDump(_dumpMix, _mixBufF, n);
            if (_writeStems && _wavSys != null)
            {
                EnsureCapacity(ref _pcm16Sys, n * 2);
                Array.Clear(_pcm16Sys, 0, n * 2);
                EnqueueWrite(AudioFileTarget.System, _pcm16Sys, n * 2);
            }
            if (_recording && _wavMix != null)
            {
                if (_mixUse32Bit) { EnsureCapacity(ref _pcm32Mix, n * 4); FloatToPcm32(_mixBufF, _pcm32Mix, n); EnqueueWrite(AudioFileTarget.Mix, _pcm32Mix, n * 4); }
                else { EnsureCapacity(ref _pcm16Mix, n * 2); FloatToPcm16(_mixBufF, _pcm16Mix, n); EnqueueWrite(AudioFileTarget.Mix, _pcm16Mix, n * 2); }
            }
        }

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
            return (int)BitOperations.RoundUpToPowerOf2((uint)n);
        }

        // Caller holds _micRingLock; no logging inside (lock discipline: indices only).
        // Returns samples dropped; caller Warns outside the lock.
        private long EnsureRingCapacity(int roomNeeded)
        {
            Debug.Assert(roomNeeded >= 0, "EnsureRingCapacity roomNeeded must be >= 0");
            if (_micRing.Length - _micCount >= roomNeeded) { Debug.Assert(_micCount <= _micRing.Length, "micCount must not exceed ring length"); return 0; }

            // Bounded FIFO (~2 s at current rate): grow up to cap, then drop-oldest + count.
            // Prevents unbounded growth when mic clock runs faster than loopback (drift).
            int cap = Math.Max(_micRing.Length, _outRate * _outChannels * 2);
            int want = _micCount + roomNeeded;
            if (want <= cap)
            {
                int newLen = Math.Min(cap, NextPow2(want));
                if (newLen <= _micRing.Length) return 0;
                var newBuf = new float[newLen];
                for (int i = 0; i < _micCount; i++)
                    newBuf[i] = _micRing[(_micR + i) % _micRing.Length];
                _micRing = newBuf;
                _micR = 0;
                _micW = _micCount;
                Debug.Assert(_micCount <= _micRing.Length, "micCount must not exceed ring length");
                return 0;
            }

            int drop = want - cap;
            drop = Math.Min(drop, _micCount);
            if (drop > 0)
            {
                _micR = (_micR + drop) % _micRing.Length;
                _micCount -= drop;
            }
            long dropped = want - cap;
            Interlocked.Add(ref _micOverrunBlocks, dropped);
            Debug.Assert(_micCount <= _micRing.Length, "micCount must not exceed ring length");
            return dropped;
        }

        private void OpenLog()
        {
            try
            {
                _logBytesWritten = 0;
                _logTruncated = false;
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

                    // After cap: suppress routine INFO but keep WARN/ERROR and session-end summary
                    // so the tail (underrun/overrun counts) is never lost.
                    if (_logTruncated && level == "INFO" &&
                        msg.IndexOf("Session end", StringComparison.OrdinalIgnoreCase) < 0 &&
                        msg.IndexOf("Recording stopped", StringComparison.OrdinalIgnoreCase) < 0 &&
                        msg.IndexOf("underrun", StringComparison.OrdinalIgnoreCase) < 0 &&
                        msg.IndexOf("Overrun", StringComparison.OrdinalIgnoreCase) < 0 &&
                        msg.IndexOf("Silenced", StringComparison.OrdinalIgnoreCase) < 0)
                        return;

                    var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {level} {where}: {msg}";
                    _logBytesWritten += line.Length * 2; // Rough estimate

                    if (_logBytesWritten > MaxLogSizeBytes && !_logTruncated)
                    {
                        _log.WriteLine("[LOG TRUNCATED - Maximum size reached, INFO suppressed, WARN/ERROR retained]");
                        _logTruncated = true;
                        return;
                    }

                    _log.WriteLine(line);
                }
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
        
        private void RaiseStatus(
            EngineStatusKind kind, string message, bool success = false,
            string? outputPathSystem = null, string? outputPathMic = null,
            string? outputPathMix = null, string? outputPathMp3 = null)
            => Status?.Invoke(this, new EngineStatusEventArgs(
                kind, message, success, outputPathSystem, outputPathMic, outputPathMix, outputPathMp3));

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
            catch { return path; }
        }
    }
}
