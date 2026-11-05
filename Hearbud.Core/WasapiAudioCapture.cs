// Windows capture backend: NAudio WASAPI, moved verbatim out of RecorderEngine.
// Owns every NAudio object (MMDevice, WasapiRecorder, notification client) so
// the engine only ever sees the IAudioCaptureFactory seam.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Hearbud
{
    public sealed class WasapiAudioCaptureFactory : IAudioCaptureFactory
    {
        public IReadOnlyList<AudioDeviceInfo> GetLoopbackDevices() =>
            Enumerate(DataFlow.Render, Role.Multimedia);

        public IReadOnlyList<AudioDeviceInfo> GetMicrophoneDevices() =>
            Enumerate(DataFlow.Capture, Role.Communications);

        private static IReadOnlyList<AudioDeviceInfo> Enumerate(DataFlow flow, Role role)
        {
            var list = new List<AudioDeviceInfo>();
            try
            {
                using var e = new MMDeviceEnumerator();
                string? defId = null;
                try
                {
                    using var d = e.GetDefaultAudioEndpoint(flow, role);
                    defId = d?.ID;
                }
                catch { /* no default — all entries non-default */ }

                var endpoints = e.EnumerateAudioEndPoints(flow, DeviceState.Active);
                try
                {
                    foreach (var dev in endpoints)
                    {
                        try
                        {
                            using (dev)
                                list.Add(new AudioDeviceInfo(dev.ID, dev.FriendlyName, dev.ID == defId));
                        }
                        catch { /* skip unreadable endpoints */ }
                    }
                }
                finally
                {
                    if (endpoints is IDisposable d) d.Dispose();
                }
            }
            catch { /* enumeration must never throw to hosts */ }
            return list;
        }

        public string GetDeviceName(string deviceId)
        {
            try
            {
                using var e = new MMDeviceEnumerator();
                using var dev = e.GetDevice(deviceId);
                return dev.FriendlyName;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Audio device not found: {deviceId}", ex);
            }
        }

        public IAudioRecorder OpenLoopback(
            string deviceId, int sampleRate, int channels,
            Action<string> log, Action<Exception> onError)
        {
            using var mmde = new MMDeviceEnumerator();
            MMDevice dev;
            try
            {
                dev = mmde.GetDevice(deviceId);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Loopback device not found: {deviceId}", ex);
            }

            var target = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
            WasapiRecorder rec;
            try
            {
                rec = new WasapiRecorderBuilder()
                    .WithDevice(dev)
                    .WithLoopbackCapture()
                    .WithFormat(target)
                    .WithLowLatency(true)
                    .WithMmcssThreadPriority("Pro Audio")
                    .Build();
            }
            catch
            {
                try { dev.Dispose(); } catch { }
                throw;
            }

            log($"Loopback low-latency: {rec.LowLatencyActive}" +
                (rec.LowLatencyActive ? "" : $" ({rec.LowLatencyUnavailableReason})"));
            return new WasapiAudioRecorder(rec, dev, channels, onError);
        }

        public IAudioRecorder OpenMicrophone(
            string deviceId, int sampleRate, int channels,
            Action<string> log, Action<Exception> onError)
        {
            using var mmde = new MMDeviceEnumerator();
            MMDevice dev;
            try
            {
                dev = mmde.GetDevice(deviceId);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Mic device not found: {deviceId}", ex);
            }

            var target = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
            // Raw mode bypasses vendor APOs (AGC/NS pumping); fall back to
            // cooked capture when the driver rejects raw mode.
            WasapiRecorder rec;
            try
            {
                rec = new WasapiRecorderBuilder()
                    .WithDevice(dev)
                    .WithFormat(target)
                    .WithLowLatency(true)
                    .WithMmcssThreadPriority("Pro Audio")
                    .WithRawMode()
                    .Build();
                log("Mic opened in raw mode");
            }
            catch (Exception ex)
            {
                log($"Raw mic mode rejected, falling back to standard capture. {ex.Message}");
                try
                {
                    rec = new WasapiRecorderBuilder()
                        .WithDevice(dev)
                        .WithFormat(target)
                        .WithLowLatency(true)
                        .WithMmcssThreadPriority("Pro Audio")
                        .Build();
                }
                catch
                {
                    try { dev.Dispose(); } catch { }
                    throw;
                }
            }

            log($"Mic low-latency: {rec.LowLatencyActive}" +
                (rec.LowLatencyActive ? "" : $" ({rec.LowLatencyUnavailableReason})"));
            return new WasapiAudioRecorder(rec, dev, channels, onError);
        }

        public IDisposable? StartDeviceNotifications(Action onChanged)
        {
            var e = new MMDeviceEnumerator();
            try
            {
                // useSynchronizationContext: false — the engine is headless, so
                // events arrive on the audio worker thread and MUST NOT BLOCK.
                // Hosts marshal to their UI thread (see DevicesChanged docs).
                var client = e.CreateNotificationClient(false);
                client.DeviceAdded += (_, _) => onChanged();
                client.DeviceRemoved += (_, _) => onChanged();
                client.DeviceStateChanged += (_, _) => onChanged();
                client.DefaultDeviceChanged += (_, _) => onChanged();
                return new DeviceNotifications(client, e);
            }
            catch
            {
                try { e.Dispose(); } catch { }
                throw;
            }
        }

        private sealed class DeviceNotifications : IDisposable
        {
            private MMDeviceNotificationClient? _client;
            private MMDeviceEnumerator? _enum;

            public DeviceNotifications(MMDeviceNotificationClient client, MMDeviceEnumerator e)
            { _client = client; _enum = e; }

            public void Dispose()
            {
                try { _client?.Dispose(); } catch { }
                _client = null;
                try { _enum?.Dispose(); } catch { }
                _enum = null;
            }
        }
    }

    // One open WASAPI leg. Retains the MMDevice (the recorder holds it — it
    // must outlive recording) and converts NAudio callbacks to float blocks.
    internal sealed class WasapiAudioRecorder : IAudioRecorder
    {
        private WasapiRecorder? _rec;
        private MMDevice? _dev;
        private readonly int _channels;
        private readonly Action<Exception> _onError;
        private volatile bool _disposed;

        public WasapiAudioRecorder(
            WasapiRecorder rec, MMDevice dev, int channels, Action<Exception> onError)
        {
            _rec = rec;
            _dev = dev;
            _channels = channels;
            _onError = onError;
            DeviceName = dev.FriendlyName;
            rec.DataAvailable += OnData;
            rec.RecordingStopped += OnStopped;
        }

        public string DeviceName { get; }

        public event EventHandler<AudioBlockEventArgs>? BlockAvailable;
        public event EventHandler<CaptureStoppedEventArgs>? RecordingStopped;

        public void StartRecording() => _rec?.StartRecording();

        public void StopRecording() => _rec?.StopRecording();

        // Runs on NAudio's MMCSS capture thread. Must never block, touch the
        // disk, resample, or throw — a throw kills the capture thread.
        private void OnData(ReadOnlySpan<byte> span, AudioClientBufferFlags flags,
            long devicePosition, long qpcPosition)
        {
            var sink = BlockAvailable;
            if (sink == null || _disposed) return;
            try
            {
                int frames = span.Length / (_channels * 4);
                if (frames <= 0) return;
                var f = new float[frames * _channels];
                if (!flags.HasFlag(AudioClientBufferFlags.Silent))
                    span.Slice(0, frames * _channels * 4).CopyTo(MemoryMarshal.AsBytes(f.AsSpan()));
                // Silent/absent audio is data too: zeros keep the files aligned.
                sink(this, new AudioBlockEventArgs(f, frames, qpcPosition));
            }
            catch (Exception ex)
            {
                try { _onError(ex); } catch { }
            }
        }

        private void OnStopped(object? sender, StoppedEventArgs e)
        {
            if (_disposed) return;
            try { RecordingStopped?.Invoke(this, new CaptureStoppedEventArgs(e?.Exception)); }
            catch (Exception ex)
            {
                try { _onError(ex); } catch { }
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _rec?.StopRecording(); } catch { }
            try
            {
                if (_rec is IAsyncDisposable ad)
                    ad.DisposeAsync().AsTask().Wait(2000);
                else _rec?.Dispose();
            }
            catch { }
            _rec = null;
            try { _dev?.Dispose(); } catch { }
            _dev = null;
        }
    }
}
