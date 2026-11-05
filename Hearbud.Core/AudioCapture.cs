// The capture seam: everything the engine needs from an audio backend.
// A recorder delivers interleaved float32 blocks stamped in 100-ns units on a
// monotonic clock (WASAPI passes QPC through; other backends convert to the
// same domain). Backend capture threads obey the old inline-callback rules:
// copy, stamp, raise BlockAvailable — never block, never throw.

using System;
using System.Collections.Generic;

namespace Hearbud
{
    // One captured block: Samples holds Frames * channel-count interleaved
    // float32 samples; Qpc100Ns is the 100-ns arrival stamp. Silent/absent
    // audio arrives as zeros so the files stay aligned.
    public sealed class AudioBlockEventArgs : EventArgs
    {
        public AudioBlockEventArgs(float[] samples, int frames, long qpc100Ns)
        { Samples = samples; Frames = frames; Qpc100Ns = qpc100Ns; }
        public float[] Samples { get; }
        public int Frames { get; }
        public long Qpc100Ns { get; }
    }

    public sealed class CaptureStoppedEventArgs : EventArgs
    {
        public CaptureStoppedEventArgs(Exception? error) { Error = error; }
        public Exception? Error { get; }
    }

    // One open capture leg (loopback or microphone). The backend owns every
    // native object; disposing the recorder releases them all.
    public interface IAudioRecorder : IDisposable
    {
        string DeviceName { get; }
        event EventHandler<AudioBlockEventArgs>? BlockAvailable;
        event EventHandler<CaptureStoppedEventArgs>? RecordingStopped;
        void StartRecording();
        void StopRecording();
    }

    // Opens recorders, enumerates endpoints, and watches for device changes.
    // Open* resolve the device id on every call (endpoints may be re-enumerated
    // between attempts) and throw InvalidOperationException when the id is
    // unknown. log carries open diagnostics ("raw mode", "low-latency", ...);
    // onError reports capture-thread failures the backend caught.
    public interface IAudioCaptureFactory
    {
        IReadOnlyList<AudioDeviceInfo> GetLoopbackDevices();
        IReadOnlyList<AudioDeviceInfo> GetMicrophoneDevices();
        string GetDeviceName(string deviceId);
        IAudioRecorder OpenLoopback(
            string deviceId, int sampleRate, int channels,
            Action<string> log, Action<Exception> onError);
        IAudioRecorder OpenMicrophone(
            string deviceId, int sampleRate, int channels,
            Action<string> log, Action<Exception> onError);
        // Null when the backend has no device-change notifications.
        IDisposable? StartDeviceNotifications(Action onChanged);
    }

    public static class AudioCapture
    {
        // The factory used by RecorderEngine's parameterless construction and
        // static enumeration. WASAPI unless a host selects another backend at
        // startup (set once, before any engine use — the Avalonia app sets the
        // macOS factory on macOS).
        public static IAudioCaptureFactory Default { get; set; } = new WasapiAudioCaptureFactory();
    }
}
