// ----------------------------------------------------------------------------------------------------
// PURPOSE
// ----------------------------------------------------------------------------------------------------
// This class records Windows system audio (WASAPI loopback) and, optionally, a microphone, in real-time.
// It writes three WAV files during capture:
//   1) <base>-system.wav  - the raw system/loopback stream
//   2) <base>-mic.wav     - the raw microphone stream (converted to match loopback format)
//   3) <base>-mix.wav     - a simple averaged mix of system + mic (with user-controlled gains)
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
        public string LoopbackDeviceId { get; set; } = "";
        public string? MicDeviceId { get; set; }
        public bool IncludeMic { get; set; } = true; 
        public int Mp3BitrateKbps { get; set; } = 192; 
    }

    // --- ASYNC WRITER DEFINITIONS ---
    internal enum AudioFileTarget { System, Mic, Mix }

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

        /// <summary>
        /// Gets a value indicating whether the engine is currently recording to disk.
        /// </summary>
        public bool IsRecording => _recording;

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

        // Writers are now accessed ONLY by the background thread
        private WaveWriter? _wavSys;
        private WaveWriter? _wavMic;
        private WaveWriter? _wavMix;
        private readonly bool _mixUse32Bit = true; 

        // Async Write Queue
        private BlockingCollection<AudioWriteJob>? _writeQueue;
        private Task? _writeTask;
        private long _droppedBlocks = 0;
        private Exception? _writerException;

        private string _pathSys = "";
        private string _pathMic = "";
        private string _pathMix = "";
        private string _pathMp3 = "";
        private int _kbps = 192;

        private string _logPath = "";
        private StreamWriter? _log;
        private const long MaxLogSizeBytes = 10 * 1024 * 1024; // 10 MB
        private long _logBytesWritten = 0;

        // Buffers
        private float[] _loopBufF     = new float[BlockFrames * 8];
        private float[] _micInBuf     = new float[BlockFrames * 8];
        private float[] _micConvBuf   = new float[BlockFrames * 8];
        private float[] _tmpMicBlock  = new float[BlockFrames * 8];
        private float[] _mixBufF      = new float[BlockFrames * 8];
        
        // Reusable scratch buffer for the resampler (Performance Fix)
        private float[] _resampleScratch = new float[BlockFrames * 8];

        private byte[]  _pcm16Sys     = new byte[BlockFrames * 8 * 2];
        private byte[]  _pcm16Mic     = new byte[BlockFrames * 8 * 2];
        private byte[]  _pcm16Mix     = new byte[BlockFrames * 8 * 2];
        private byte[]  _pcm32Mix     = new byte[BlockFrames * 8 * 4];

        private readonly object _micRingLock = new();
        private readonly object _logLock = new();
        private float[] _micRing = new float[48000 * 4];
        private int _micR, _micW, _micCount;

        private static readonly double _tickMs = 1000.0 / Stopwatch.Frequency;
        private long _lastLoopTick = 0;
        private const int LoopSilentMsThreshold = 200; 

        private string _loopDevName = "";
        private string _micDevName  = "(none)";
        private long _micUnderrunBlocks = 0;
        private long _loopBlockCounter = 0;
        private double _lastMicBacklogSec = 0.0;
        private double _micBacklogSecMax = 0.0;
        private const int BacklogLogEveryNBlocks = 50;

        private const double LevelThrottleMs = 50.0; 
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

        public async Task MonitorAsync(RecorderStartOptions opts)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(RecorderEngine));
            StopInternal(fullStop: true);
            await OpenDevicesAsync(opts);

            if (!_monitoring)
            {
                _loopIn!.DataAvailable += OnLoopbackData;
                if (_micIn != null) _micIn.DataAvailable += OnMicData;

                _loopCap!.Start();
                _micCap?.Start();

                _monitoring = true;
                _lastLoopTick = Stopwatch.GetTimestamp();
                _lastLevelTickMic = _lastLevelTickSys = _lastLoopTick; 
                _peakSinceLastMic = _peakSinceLastSys = 0f;
                _rmsSumSinceLastMic = _rmsSumSinceLastSys = 0.0;
                _rmsCountSinceLastMic = _rmsCountSinceLastSys = 0;
                _clippedSinceLastMic = _clippedSinceLastSys = false;

                RaiseStatus(EngineStatusKind.Info, "Monitoring…");
                Info("Monitoring started");
            }
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

            // Create writers
            _wavSys = new WaveWriter(_pathSys, new CSCore.WaveFormat(_outRate, 16, _outChannels));
            _wavMic = new WaveWriter(_pathMic, new CSCore.WaveFormat(_outRate, 16, _outChannels));
            _wavMix = new WaveWriter(_pathMix, new CSCore.WaveFormat(_outRate, _mixUse32Bit ? 32 : 16, _outChannels));

            // Initialize Async Write Queue and Task
            // Capacity is set to handle ~10 seconds of buffering if disk stalls completely.
            _writeQueue = new BlockingCollection<AudioWriteJob>(
                Math.Clamp(CalculateOptimalQueueSize(), DefaultQueueCapacity, MaxQueueCapacity)); 
            _writeTask = Task.Factory.StartNew(DiskWriteLoop, TaskCreationOptions.LongRunning);

            lock (_micRingLock) { _micR = _micW = _micCount = 0; }
            _micUnderrunBlocks = 0;
            _loopBlockCounter = 0;
            _lastMicBacklogSec = 0.0;
            _micBacklogSecMax = 0.0;

            _recording = true;
            RaiseStatus(EngineStatusKind.Info, "Recording to WAV…");
            Info("Recording started");
        }

        public async Task StopAsync(CancellationToken cancellationToken = default)
        {
            if (_disposed) return;

            _recording = false;

            StopInternal(fullStop: false);

            // 1. Drain the write queue.
            // We tell the queue no more items are coming, then wait for the background thread
            // to finish writing everything currently in the buffer.
            if (_writeQueue != null && _writeTask != null)
            {
                Info("Finishing background writes...");
                _writeQueue.CompleteAdding();

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

            // 2. Now it is safe to close the files (WAV headers are updated on Dispose)
            bool okSys = false, okMic = false, okMix = false, okMp3 = false;
            try { _wavSys?.Dispose(); okSys = File.Exists(_pathSys) && new FileInfo(_pathSys).Length > 0; } catch { }
            try { _wavMic?.Dispose(); okMic = File.Exists(_pathMic) && new FileInfo(_pathMic).Length > 0; } catch { }
            try { _wavMix?.Dispose(); okMix = File.Exists(_pathMix) && new FileInfo(_pathMix).Length > 0; } catch { }
            _wavSys = null; _wavMic = null; _wavMix = null;
            
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

            Info($"Mic ring underruns: {_micUnderrunBlocks}. Peak mic backlog: {_micBacklogSecMax:F4}s");
            if (_droppedBlocks > 0)
            {
                Warn($"Dropped {_droppedBlocks} audio blocks due to disk I/O stall");
            }
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

        public void StopMonitor() => StopInternal(fullStop: true);

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try { _wavSys?.Dispose(); } catch { }
            try { _wavMic?.Dispose(); } catch { }
            try { _wavMix?.Dispose(); } catch { }
            _wavSys = null; _wavMic = null; _wavMix = null;

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
            TryDispose(ref _log);
        }

        // ==========================================
        // BACKGROUND WRITE LOOP
        // ==========================================
        // This runs on a separate thread. It pulls data chunks from the queue and writes them to disk.
        // This ensures that slow disk I/O never blocks the audio callback methods.
        private void DiskWriteLoop()
        {
            if (_writeQueue == null) return;

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
                        // We swallow write errors here to keep the thread alive for other files, 
                        // but strictly we should log this.
                        Debug.WriteLine($"Disk write error: {ex.Message}");
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
        }

        private int CalculateOptimalQueueSize()
        {
            // Based on sample rate and block size, calculate ~10 seconds of buffer
            return Math.Max(DefaultQueueCapacity, (_outRate * _outChannels * 10) / (BlockFrames * _outChannels));
        }

        private void EnqueueWrite(AudioFileTarget target, byte[] sourceData, int count)
        {
            if (!_recording || _writeQueue == null || _writeQueue.IsAddingCompleted) return;

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

            int maxRetries = 3;
            int retryDelayMs = 250;

            for (int attempt = 0; attempt < maxRetries; attempt++)
            {
                try
                {
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

                    return;
                }
                catch (CSCore.CoreAudioAPI.CoreAudioAPIException ex)
                {
                    if (ex.HResult != unchecked((int)0x88890004))
                        throw;

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
            if (_monitoring)
            {
                try { _loopIn!.DataAvailable -= OnLoopbackData; } catch { }
                try { if (_micIn != null) _micIn.DataAvailable -= OnMicData; } catch { }

                TryDispose(ref _loopIn);
                TryDispose(ref _micIn);
                TryDispose(ref _loopSrc);
                TryDispose(ref _micSrc);

                TryStopDispose(ref _loopCap);
                TryStopDispose(ref _micCap);
                _monitoring = false;
            }

            if (fullStop) Info("Stopped.");
        }

        // ==========================================
        // AUDIO CALLBACKS
        // ==========================================

        private void OnMicData(object? sender, DataAvailableEventArgs e)
        {
            if (_micSrc == null || _micIn == null || _micCap == null) return;
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
                int conv = ConvertToTarget(_micInBuf, got, _micSrc.WaveFormat, _outRate, _outChannels, ref _micConvBuf, ref _resampleScratch);

                long nowTicksA = Stopwatch.GetTimestamp();
                double msSinceLoop = (nowTicksA - _lastLoopTick) * _tickMs;
                bool loopSilent = msSinceLoop > LoopSilentMsThreshold;

                // --- Metering ---
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

                // --- Sync Logic ---
                if (loopSilent)
                {
                    lock (_micRingLock)
                    {
                        _micR = _micW;
                        _micCount = 0;
                    }
                }
                else
                {
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

                // --- Write Mic WAV (Async) ---
                if (_recording && _wavMic != null)
                {
                    EnsureCapacity(ref _pcm16Mic, conv * 2);
                    FloatToPcm16(_micConvBuf, _pcm16Mic, conv); 
                    // Queue for background writing instead of blocking here
                    EnqueueWrite(AudioFileTarget.Mic, _pcm16Mic, conv * 2);
                }

                // --- Mic-Only Drive (if loopback silent) ---
                if (_recording && _wavMix != null && _wavSys != null && loopSilent)
                {
                    // Write zeros to System
                    EnsureCapacity(ref _pcm16Sys, conv * 2);
                    Array.Clear(_pcm16Sys, 0, conv * 2);
                    EnqueueWrite(AudioFileTarget.System, _pcm16Sys, conv * 2);

                    // Write Mic to Mix
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
                        EnqueueWrite(AudioFileTarget.Mix, _pcm32Mix, conv * 4);
                    }
                    else
                    {
                        EnsureCapacity(ref _pcm16Mix, conv * 2);
                        FloatToPcm16(_mixBufF, _pcm16Mix, conv);
                        EnqueueWrite(AudioFileTarget.Mix, _pcm16Mix, conv * 2);
                    }
                }
            }
            catch (CSCore.CoreAudioAPI.CoreAudioAPIException ex) when (ex.HResult == unchecked((int)0x88890004))
            {
                RaiseStatus(EngineStatusKind.Error, "Audio device disconnected");
                StopInternal(fullStop: true);
            }
            catch (Exception ex) { Error("OnMicData", ex); }
        }

        private void OnLoopbackData(object? sender, DataAvailableEventArgs e)
        {
            if (_loopSrc == null || _loopCap == null || _loopIn == null) return;

            try
            {
                _lastLoopTick = Stopwatch.GetTimestamp();

                int bytesPerFrame = _loopIn.WaveFormat.BlockAlign; 
                if (bytesPerFrame <= 0) return;

                int frames = e.ByteCount / bytesPerFrame;
                if (frames <= 0) return;

                int wantFloats = frames * _outChannels;
                EnsureCapacity(ref _loopBufF, wantFloats);

                int gotLoop = ReadExactSamples(_loopSrc, _loopBufF, wantFloats);
                if (gotLoop <= 0) return;

                // --- Metering ---
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

                // --- Write System WAV (Async) ---
                if (_recording && _wavSys != null)
                {
                    EnsureCapacity(ref _pcm16Sys, gotLoop * 2);
                    FloatToPcm16(_loopBufF, _pcm16Sys, gotLoop);
                    EnqueueWrite(AudioFileTarget.System, _pcm16Sys, gotLoop * 2);
                }

                // --- Diagnostics ---
                int snapshotMicCount;
                lock (_micRingLock)
                {
                    snapshotMicCount = _micCount;
                }
                double backlogSec = (double)snapshotMicCount / (_outChannels * _outRate);
                _lastMicBacklogSec = backlogSec;
                if (backlogSec > _micBacklogSecMax) _micBacklogSecMax = backlogSec;

                // --- Mixing ---
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

                EnsureCapacity(ref _mixBufF, gotLoop);
                for (int i = 0; i < gotLoop; i++)
                {
                    float s = (float)(_loopBufF[i] * LoopGain);
                    float m = (float)(_tmpMicBlock[i] * MicGain);
                    float a = (s + m) * 0.5f; 
                    a = SoftClipIfNeeded(a);
                    _mixBufF[i] = a;
                }

                // --- Write Mix WAV (Async) ---
                if (_recording && _wavMix != null)
                {
                    if (_mixUse32Bit)
                    {
                        EnsureCapacity(ref _pcm32Mix, gotLoop * 4);
                        FloatToPcm32(_mixBufF, _pcm32Mix, gotLoop);
                        EnqueueWrite(AudioFileTarget.Mix, _pcm32Mix, gotLoop * 4);
                    }
                    else
                    {
                        EnsureCapacity(ref _pcm16Mix, gotLoop * 2);
                        FloatToPcm16(_mixBufF, _pcm16Mix, gotLoop); 
                        EnqueueWrite(AudioFileTarget.Mix, _pcm16Mix, gotLoop * 2);
                    }
                }
            }
            catch (CSCore.CoreAudioAPI.CoreAudioAPIException ex) when (ex.HResult == unchecked((int)0x88890004))
            {
                RaiseStatus(EngineStatusKind.Error, "Audio device disconnected");
                StopInternal(fullStop: true);
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

        // Modified: Now takes 'ref scratch' to avoid "new float[]" allocations in the loop
        private static int ConvertToTarget(
            float[] src, int floatCount, CSCore.WaveFormat srcFmt,
            int dstRate, int dstCh, 
            ref float[] dst, ref float[] scratch)
        {
            int srcCh = srcFmt.Channels;
            int srcRate = srcFmt.SampleRate;

            int srcFrames = floatCount / srcCh;
            if (srcFrames <= 0) return 0;

            int dstFrames = (srcRate == dstRate) ? srcFrames
                          : (int)((long)srcFrames * dstRate / srcRate);

            EnsureCapacity(ref dst, Math.Max(dstCh, srcCh) * dstFrames);

            // Optimization: Reuse existing buffers instead of creating 'temp'
            // If no resample needed, we point temp to src.
            // If resample needed, we use the scratch buffer provided by the caller.
            float[] temp = src;
            int tempFrames = srcFrames;

            if (srcRate != dstRate)
            {
                EnsureCapacity(ref scratch, srcCh * dstFrames);
                temp = scratch;

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

        private static float SoftClipIfNeeded(float x)
        {
            if (x > 1f || x < -1f) x = MathF.Tanh(x);
            if (x > 1f) x = 1f; else if (x < -1f) x = -1f;
            return x;
        }

        private static readonly ThreadLocal<Random> _rng =
            new ThreadLocal<Random>(() => new Random(
                HashCode.Combine(
                    Environment.TickCount64,
                    Thread.CurrentThread.ManagedThreadId,
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
                
                // Use long for intermediate calculation to avoid overflow
                long scaled = (long)Math.Round(v * 2147483647.0);
                scaled = Math.Clamp(scaled, int.MinValue, int.MaxValue);
                int s = (int)scaled;
                
                dst[j++] = (byte)(s & 0xFF);
                dst[j++] = (byte)((s >> 8) & 0xFF);
                dst[j++] = (byte)((s >> 16) & 0xFF);
                dst[j++] = (byte)((s >> 24) & 0xFF);
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
            _micW = _micCount;
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
