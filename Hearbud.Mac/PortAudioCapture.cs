// macOS capture backend: PortAudio input streams, behind the IAudioCaptureFactory seam.
// CoreAudio has no loopback endpoint API, so system capture is a routing setup:
// the user routes output through BlackHole (or equivalent) and picks that
// device as the "loopback" device. Both Open* methods therefore open INPUT
// devices by id; only the log/kind labels differ.
//
// Format: every stream is opened AT the engine's requested format (48 kHz
// float32 stereo) with SampleFormat.Float32, so no resampling code lives here.
// If stereo open fails, one mono open is attempted (duplicated to stereo);
// if that fails too the open throws an actionable error and the engine
// retries/aborts. Stamps are 100-ns ticks converted from Stopwatch, which is
// mach-backed monotonic on macOS, matching the seam's clock domain.
//
// Device ids are device names: PortAudio exposes no stable endpoint ids and
// indices shift on plug/unplug, so the id is resolved to a fresh index on
// every open. (Two identical devices sharing a name resolve to the first.)

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using PortAudioSharp;

namespace Hearbud
{
    // Process-wide PortAudio lifetime: initialized once on first use, never
    // terminated (streams may outlive any one engine; Terminate with open
    // streams is undefined). Failures are NOT cached — a later retry after a
    // plug/unplug may succeed.
    internal static class PortAudioHost
    {
        private static readonly object _sync = new();
        private static bool _initialized;

        public static void EnsureInitialized()
        {
            lock (_sync)
            {
                if (_initialized) return;
                PortAudio.Initialize();
                _initialized = true;
            }
        }
    }

    public sealed class PortAudioCaptureFactory : IAudioCaptureFactory
    {
        public IReadOnlyList<AudioDeviceInfo> GetLoopbackDevices() =>
            PreferVirtualDevices(EnumerateInputs());

        public IReadOnlyList<AudioDeviceInfo> GetMicrophoneDevices() => EnumerateInputs();

        // Names of known virtual audio drivers, which appear as ordinary
        // inputs but carry routable system audio (BlackHole et al). "loopback"
        // covers Rogue Amoeba's "Loopback Audio" plus generically named taps;
        // a physical mic is never named that.
        public static bool IsVirtualLoopbackDevice(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            return name.Contains("blackhole", StringComparison.OrdinalIgnoreCase)
                || name.Contains("soundflower", StringComparison.OrdinalIgnoreCase)
                || name.Contains("loopback", StringComparison.OrdinalIgnoreCase);
        }

        // Loopback UX: virtual drivers first, default flag on the first one
        // so hosts auto-select a real system-audio source instead of a mic.
        // Pure (no PortAudio calls) so it stays unit-testable headless.
        public static IReadOnlyList<AudioDeviceInfo> PreferVirtualDevices(
            IReadOnlyList<AudioDeviceInfo> inputs)
        {
            var ordered = new List<AudioDeviceInfo>(inputs.Count);
            foreach (var d in inputs)
                if (IsVirtualLoopbackDevice(d.Name)) ordered.Add(d);
            foreach (var d in inputs)
                if (!IsVirtualLoopbackDevice(d.Name)) ordered.Add(d);
            bool flagged = false;
            for (int i = 0; i < ordered.Count; i++)
            {
                bool isDefault = !flagged && IsVirtualLoopbackDevice(ordered[i].Name);
                if (isDefault) flagged = true;
                ordered[i] = ordered[i] with { IsDefault = isDefault };
            }
            return ordered;
        }

        private static IReadOnlyList<AudioDeviceInfo> EnumerateInputs()
        {
            var list = new List<AudioDeviceInfo>();
            try
            {
                PortAudioHost.EnsureInitialized();
                int def = PortAudio.NoDevice;
                try { def = PortAudio.DefaultInputDevice; } catch { /* no default */ }

                int count = PortAudio.DeviceCount;
                for (int i = 0; i < count; i++)
                {
                    DeviceInfo info;
                    try { info = PortAudio.GetDeviceInfo(i); } catch { continue; }
                    if (info.maxInputChannels <= 0) continue;
                    string name = info.name ?? $"Input device {i}";
                    list.Add(new AudioDeviceInfo(name, name, i == def));
                }
            }
            catch { /* enumeration must never throw to hosts */ }
            return list;
        }

        public string GetDeviceName(string deviceId) =>
            Resolve("Audio", deviceId).Info.name ?? deviceId;

        public IAudioRecorder OpenLoopback(
            string deviceId, int sampleRate, int channels,
            Action<string> log, Action<Exception> onError) =>
            OpenInput("Loopback", deviceId, sampleRate, channels, log, onError);

        public IAudioRecorder OpenMicrophone(
            string deviceId, int sampleRate, int channels,
            Action<string> log, Action<Exception> onError) =>
            OpenInput("Mic", deviceId, sampleRate, channels, log, onError);

        private static IAudioRecorder OpenInput(
            string kind, string deviceId, int sampleRate, int channels,
            Action<string> log, Action<Exception> onError)
        {
            var (index, info) = Resolve(kind, deviceId);
            string name = info.name ?? deviceId;
            int maxIn = info.maxInputChannels <= 0 ? channels : info.maxInputChannels;

            // Channel attempts: requested width first (clamped to what the
            // device reports), then mono-with-expand as a last resort. Mono
            // devices (notably Bluetooth headsets) commonly reject stereo.
            var attempts = new List<int> { Math.Min(channels, maxIn) };
            if (attempts[0] != 1) attempts.Add(1);

            Exception? lastError = null;
            foreach (int streamCh in attempts)
            {
                var param = new StreamParameters
                {
                    device = index,
                    channelCount = streamCh,
                    sampleFormat = SampleFormat.Float32,
                    suggestedLatency = info.defaultLowInputLatency,
                    hostApiSpecificStreamInfo = IntPtr.Zero,
                };

                var rec = new PortAudioRecorder(name, channels, streamCh, onError);
                PortAudioSharp.Stream? stream = null;
                try
                {
                    stream = new PortAudioSharp.Stream(
                        inParams: param,
                        outParams: null,
                        sampleRate: sampleRate,
                        framesPerBuffer: PortAudio.FramesPerBufferUnspecified,
                        streamFlags: StreamFlags.ClipOff,
                        callback: rec.Callback,
                        userData: IntPtr.Zero);
                    rec.Attach(stream);
                    stream = null; // owned by the recorder now
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    if (stream != null) try { stream.Dispose(); } catch { }
                    try { rec.Dispose(); } catch { }
                    continue;
                }

                log($"{kind} opened (PortAudio): {name} at {sampleRate} Hz/{channels}ch float32" +
                    (streamCh != channels ? $" (mono source, expanded to {channels}ch)" : ", requested format") +
                    ", no resampling");
                if (Math.Abs(info.defaultSampleRate - sampleRate) > 1)
                    log($"{kind} device native rate is {info.defaultSampleRate} Hz; " +
                        $"the host performs any needed conversion");
                return rec;
            }

            throw new InvalidOperationException(
                $"Could not open {kind} device '{name}' at {sampleRate} Hz/{channels}ch float32: " +
                $"{lastError?.Message}. Device reports {info.defaultSampleRate} Hz / {maxIn} input " +
                $"channel(s). Bluetooth headsets often can't capture at 48 kHz — " +
                $"try selecting another device.", lastError);
        }

        // Resolves the device id (a name — see the file header) to a FRESH
        // PortAudio index on every call; throws InvalidOperationException when
        // the id is unknown, per the seam contract.
        private static (int Index, DeviceInfo Info) Resolve(string kind, string deviceId)
        {
            PortAudioHost.EnsureInitialized();
            int count = PortAudio.DeviceCount;
            for (int i = 0; i < count; i++)
            {
                DeviceInfo info;
                try { info = PortAudio.GetDeviceInfo(i); } catch { continue; }
                if (info.maxInputChannels > 0 &&
                    string.Equals(info.name, deviceId, StringComparison.Ordinal))
                    return (i, info);
            }
            throw new InvalidOperationException($"{kind} device not found: {deviceId}");
        }

        // PortAudio exposes no device-change notifications: the engine treats
        // null as "no notifications" and relies on per-leg auto-rebuild.
        public IDisposable? StartDeviceNotifications(Action onChanged) => null;
    }

    // One open PortAudio input leg. Owns the native stream; disposing the
    // recorder stops and closes it. The callback delegates are held as fields
    // so the GC never collects them while native code may call back.
    internal sealed class PortAudioRecorder : IAudioRecorder
    {
        private PortAudioSharp.Stream? _stream;
        private readonly PortAudioSharp.Stream.Callback _callback;
        private readonly PortAudioSharp.Stream.FinishedCallback _finished;
        private readonly int _channels;
        private readonly int _streamChannels;
        private readonly Action<Exception> _onError;
        private volatile bool _started;
        private volatile bool _stopped;
        private volatile bool _disposed;

        public PortAudioRecorder(string deviceName, int channels, int streamChannels, Action<Exception> onError)
        {
            DeviceName = deviceName;
            _channels = channels;
            _streamChannels = streamChannels;
            _onError = onError;
            _callback = OnCallback;
            _finished = OnFinished;
        }

        public string DeviceName { get; }

        public event EventHandler<AudioBlockEventArgs>? BlockAvailable;
        public event EventHandler<CaptureStoppedEventArgs>? RecordingStopped;

        internal PortAudioSharp.Stream.Callback Callback => _callback;

        internal void Attach(PortAudioSharp.Stream stream)
        {
            _stream = stream;
            stream.SetFinishedCallback(_finished);
        }

        public void StartRecording()
        {
            var s = _stream;
            if (s == null || _disposed) return;
            s.Start();
            _started = true;
        }

        public void StopRecording()
        {
            var s = _stream;
            if (s == null || !_started || _stopped) return;
            _stopped = true;
            s.Stop();
        }

        // Runs on PortAudio's CoreAudio callback thread. Must never block,
        // touch the disk, resample, or throw — a throw kills the audio thread.
        private StreamCallbackResult OnCallback(
            IntPtr input, IntPtr output, uint frameCount,
            ref StreamCallbackTimeInfo timeInfo, StreamCallbackFlags statusFlags,
            IntPtr userData)
        {
            var sink = BlockAvailable;
            if (sink == null || _disposed) return StreamCallbackResult.Continue;
            try
            {
                // Stamp FIRST at callback entry, then copy: the stamp marks
                // arrival in 100-ns ticks (Stopwatch is mach-backed monotonic
                // on macOS; GetElapsedTime converts without overflow).
                long stamp = Stopwatch.GetElapsedTime(0, Stopwatch.GetTimestamp()).Ticks;
                int frames = frameCount > int.MaxValue ? int.MaxValue : (int)frameCount;
                if (frames <= 0) return StreamCallbackResult.Continue;
                var f = new float[frames * _channels];
                if (input != IntPtr.Zero)
                {
                    if (_streamChannels == _channels)
                    {
                        Marshal.Copy(input, f, 0, f.Length);
                    }
                    else if (_streamChannels == 1 && _channels == 2)
                    {
                        // Mono fallback: duplicate the channel into stereo.
                        var mono = new float[frames];
                        Marshal.Copy(input, mono, 0, frames);
                        for (int i = 0; i < frames; i++)
                        {
                            f[i * 2] = mono[i];
                            f[i * 2 + 1] = mono[i];
                        }
                    }
                    else
                    {
                        try { _onError(new InvalidOperationException(
                            $"Unsupported channel map {_streamChannels}->{_channels}.")); } catch { }
                        return StreamCallbackResult.Continue;
                    }
                }
                // A null input (or short read) is data too: zeros keep the
                // files aligned, mirroring the WASAPI backend's silent blocks.
                sink(this, new AudioBlockEventArgs(f, frames, stamp));
            }
            catch (Exception ex)
            {
                try { _onError(ex); } catch { }
            }
            return StreamCallbackResult.Continue;
        }

        // Fires when the stream goes inactive (Stop, Abort, or device loss):
        // maps onto RecordingStopped like the WASAPI backend. The engine
        // ignores this while stopping and rebuilds the leg otherwise.
        private void OnFinished(IntPtr userData)
        {
            if (_disposed) return;
            try { RecordingStopped?.Invoke(this, new CaptureStoppedEventArgs(null)); }
            catch (Exception ex)
            {
                try { _onError(ex); } catch { }
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { StopRecording(); } catch { }
            var s = _stream;
            _stream = null;
            try { s?.Dispose(); } catch { }
        }
    }

    // Host-side OS selection entry point for the macOS backend.
    // Core's AudioCapture.Default intentionally stays WASAPI: hosts pick the
    // factory by OS (e.g. OperatingSystem.IsMacOS() ? MacAudioCapture.Factory
    // : AudioCapture.Default).
    public static class MacAudioCapture
    {
        public static IAudioCaptureFactory Factory { get; } = new PortAudioCaptureFactory();
    }
}
