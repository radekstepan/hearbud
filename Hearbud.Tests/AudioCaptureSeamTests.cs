// Guards the capture seam: the engine must drive any IAudioCaptureFactory,
// not just NAudio. Fully deterministic — no audio hardware, no timing asserts.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Xunit;

namespace Hearbud.Tests
{
    internal sealed class FakeRecorder : IAudioRecorder
    {
        public FakeRecorder(string name) => DeviceName = name;
        public string DeviceName { get; }
        public event EventHandler<AudioBlockEventArgs>? BlockAvailable;
        public event EventHandler<CaptureStoppedEventArgs>? RecordingStopped;
        public int StartCalls;
        public int StopCalls;
        public bool Disposed;
        public bool HadSubscribersOnStart;
        public void StartRecording()
        {
            StartCalls++;
            HadSubscribersOnStart = BlockAvailable != null && RecordingStopped != null;
        }
        public void StopRecording() => StopCalls++;
        public void Dispose() => Disposed = true;
        public void Emit(float[] samples, int frames, long qpc100Ns) =>
            BlockAvailable?.Invoke(this, new AudioBlockEventArgs(samples, frames, qpc100Ns));
    }

    internal sealed class FakeCaptureFactory : IAudioCaptureFactory
    {
        public readonly List<(string Kind, string Id, int Rate, int Ch)> Opens = new();
        public readonly FakeRecorder Loop = new("Loop");
        public readonly FakeRecorder Mic = new("Mic");
        public bool ThrowOnMicResolve;
        public int NotificationStarts;

        public IReadOnlyList<AudioDeviceInfo> GetLoopbackDevices() =>
            new[] { new AudioDeviceInfo("loop-id", "Loop", true) };
        public IReadOnlyList<AudioDeviceInfo> GetMicrophoneDevices() =>
            new[] { new AudioDeviceInfo("mic-id", "Mic", true) };

        public string GetDeviceName(string deviceId)
        {
            if (deviceId == "mic-id" && ThrowOnMicResolve)
                throw new InvalidOperationException("Audio device not found: mic-id");
            if (deviceId != "loop-id" && deviceId != "mic-id")
                throw new InvalidOperationException($"Audio device not found: {deviceId}");
            return deviceId == "loop-id" ? "Loop" : "Mic";
        }

        public IAudioRecorder OpenLoopback(
            string deviceId, int sampleRate, int channels,
            Action<string> log, Action<Exception> onError)
        {
            Opens.Add(("loop", deviceId, sampleRate, channels));
            return Loop;
        }

        public IAudioRecorder OpenMicrophone(
            string deviceId, int sampleRate, int channels,
            Action<string> log, Action<Exception> onError)
        {
            Opens.Add(("mic", deviceId, sampleRate, channels));
            return Mic;
        }

        public IDisposable? StartDeviceNotifications(Action onChanged)
        {
            NotificationStarts++;
            return null;
        }
    }

    public sealed class AudioCaptureSeamTests
    {
        [Fact]
        public async Task MonitorAsync_OpensBothLegsThroughFactory_AndStopMonitorStopsThem()
        {
            var factory = new FakeCaptureFactory();
            using var engine = new RecorderEngine(factory);

            await engine.MonitorAsync(new RecorderStartOptions
            {
                LoopbackDeviceId = "loop-id",
                MicDeviceId = "mic-id",
            });

            Assert.Equal(2, factory.Opens.Count);
            Assert.Equal(("loop", "loop-id", 48000, 2), factory.Opens[0]);
            Assert.Equal(("mic", "mic-id", 48000, 2), factory.Opens[1]);
            Assert.Equal(1, factory.Loop.StartCalls);
            Assert.Equal(1, factory.Mic.StartCalls);
            Assert.True(factory.Loop.HadSubscribersOnStart);
            Assert.True(factory.Mic.HadSubscribersOnStart);
            Assert.Equal(1, factory.NotificationStarts);

            engine.StopMonitor();

            Assert.Equal(1, factory.Loop.StopCalls);
            Assert.Equal(1, factory.Mic.StopCalls);
            Assert.True(factory.Loop.Disposed);
            Assert.True(factory.Mic.Disposed);
        }

        [Fact]
        public async Task MonitorAsync_MicMissing_ContinuesLoopbackOnly()
        {
            var factory = new FakeCaptureFactory { ThrowOnMicResolve = true };
            using var engine = new RecorderEngine(factory);

            await engine.MonitorAsync(new RecorderStartOptions
            {
                LoopbackDeviceId = "loop-id",
                MicDeviceId = "mic-id",
            });

            Assert.Single(factory.Opens);
            Assert.Equal(1, factory.Loop.StartCalls);
            Assert.Equal(0, factory.Mic.StartCalls);

            engine.StopMonitor();
        }

        [Fact]
        public async Task MonitorAsync_BadLoopbackId_ThrowsInvalidOperation()
        {
            var factory = new FakeCaptureFactory();
            using var engine = new RecorderEngine(factory);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                engine.MonitorAsync(new RecorderStartOptions { LoopbackDeviceId = "nope" }));
        }

        [Fact]
        public void StaticDeviceEnumeration_DoesNotThrow()
        {
            // Real backend on Windows, empty lists where WASAPI is unavailable.
            Assert.NotNull(RecorderEngine.GetLoopbackDevices());
            Assert.NotNull(RecorderEngine.GetMicrophoneDevices());
        }

        [Fact]
        public void AudioCaptureDefault_CanBeSelectedByHosts()
        {
            // Hosts (e.g. the Avalonia app on macOS) point Default at their
            // backend once at startup; the engine statics follow it.
            var original = AudioCapture.Default;
            try
            {
                var factory = new FakeCaptureFactory();
                AudioCapture.Default = factory;
                Assert.Same(factory, AudioCapture.Default);
                var loop = RecorderEngine.GetLoopbackDevices();
                Assert.Single(loop);
                Assert.Equal("loop-id", loop[0].Id);
                using var engine = new RecorderEngine();
                Assert.Same(factory, GetEngineField(engine, "_capture"));
            }
            finally
            {
                AudioCapture.Default = original;
            }
        }

        private static object? GetEngineField(RecorderEngine engine, string field) =>
            typeof(RecorderEngine).GetField(field,
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?
            .GetValue(engine);

        [Fact]
        public async Task StartAsync_DuringMonitoring_WithFlowingAudio_CompletesWithoutMixerError()
        {
            // Regression test for the start-path races (single-file CopyTo vs
            // Clear, plus orphaned mixer loops on re-monitor): StartAsync runs
            // while audio flows and a previous monitor session is active —
            // exactly the real startup flow. Pre-fix this crashed with
            // "Offset and length were out of bounds" with overwhelming
            // probability (continuous emission keeps every mixer tick busy
            // across StartAsync's disk-I/O window); post-fix the session reset
            // runs with no mixer alive, so completion is deterministic.
            var factory = new FakeCaptureFactory();
            using var engine = new RecorderEngine(factory);
            var errors = new List<string>();
            engine.Status += (_, s) =>
            {
                if (s.Kind == EngineStatusKind.Error) errors.Add(s.Message);
            };

            var opts = new RecorderStartOptions { LoopbackDeviceId = "loop-id", MicDeviceId = "mic-id" };
            await engine.MonitorAsync(opts);

            using var cts = new System.Threading.CancellationTokenSource();
            var emit = EmitSineAsync(factory, cts.Token);
            try
            {
                string dir = Path.Combine(Path.GetTempPath(), $"hearbud-session-test-{Guid.NewGuid():N}");
                Directory.CreateDirectory(dir);
                try
                {
                    await engine.StartAsync(new RecorderStartOptions
                    {
                        OutputPath = Path.Combine(dir, "take"),
                        LoopbackDeviceId = "loop-id",
                        MicDeviceId = "mic-id",
                        Mp3BitrateKbps = 0, // skip encoding: WAV paths only
                    });
                    await Task.Delay(500);
                    // Stop the emitter BEFORE StopAsync (the start-path race
                    // coverage above is unaffected): the stop-phase drain
                    // waits for quiet queues, so emitting through it would
                    // burn the whole 30 s drain timeout.
                    cts.Cancel();
                    try { await emit; } catch (OperationCanceledException) { }
                    await engine.StopAsync();

                    Assert.Empty(errors);
                    Assert.Null(GetEngineField(engine, "_mixerException"));
                    Assert.True(File.Exists(Path.Combine(dir, "take-system.wav")));
                    Assert.True(File.Exists(Path.Combine(dir, "take-mic.wav")));
                    Assert.True(File.Exists(Path.Combine(dir, "take-mix.wav")));
                    Assert.True(new FileInfo(Path.Combine(dir, "take-system.wav")).Length > 1024);
                    // Audibility on both legs, not just length.
                    Assert.True(PeakOfWavData(Path.Combine(dir, "take-system.wav")) > 0.25f);
                    Assert.True(PeakOfWavData(Path.Combine(dir, "take-mic.wav")) > 0.25f);
                    Assert.True(PeakOfWavData(Path.Combine(dir, "take-mix.wav")) > 0.25f);
                }
                finally
                {
                    try { Directory.Delete(dir, recursive: true); } catch { }
                }
            }
            finally
            {
                cts.Cancel();
                try { await emit; } catch (OperationCanceledException) { }
            }
        }

        [Fact]
        public async Task StartAsync_MicOnly_EmptyLoopbackId_RecordsMicMix()
        {
            // Mic-only sessions (macOS without a virtual loopback driver):
            // an empty loopback id skips the loop leg instead of throwing,
            // and the mix carries the mic. Pre-fix this threw
            // "Loopback device not found: ".
            var factory = new FakeCaptureFactory();
            using var engine = new RecorderEngine(factory);
            var errors = new List<string>();
            engine.Status += (_, s) =>
            {
                if (s.Kind == EngineStatusKind.Error) errors.Add(s.Message);
            };

            string dir = Path.Combine(Path.GetTempPath(), $"hearbud-miconly-test-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
                using var cts = new System.Threading.CancellationTokenSource();
                var emit = EmitSineAsync(factory, cts.Token);
                try
                {
                    await engine.StartAsync(new RecorderStartOptions
                    {
                        OutputPath = Path.Combine(dir, "take"),
                        LoopbackDeviceId = "",
                        MicDeviceId = "mic-id",
                        Mp3BitrateKbps = 0, // skip encoding: WAV paths only
                    });
                    await Task.Delay(500);
                    // Stop the emitter BEFORE StopAsync: the stop-phase drain
                    // waits for quiet queues, so emitting through it would
                    // burn the whole 30 s drain timeout.
                    cts.Cancel();
                    try { await emit; } catch (OperationCanceledException) { }
                    await engine.StopAsync();

                    Assert.Empty(errors);
                    Assert.Null(GetEngineField(engine, "_mixerException"));
                    Assert.True((bool?)GetEngineField(engine, "_sessionHasMic") ?? false);
                    Assert.Single(factory.Opens);
                    Assert.Equal(("mic", "mic-id", 48000, 2), factory.Opens[0]);
                    Assert.Equal(0, factory.Loop.StartCalls);
                    Assert.True(File.Exists(Path.Combine(dir, "take-mix.wav")));
                    Assert.True(new FileInfo(Path.Combine(dir, "take-mix.wav")).Length > 1024);
                    Assert.True(File.Exists(Path.Combine(dir, "take-mic.wav")));
                    // Audibility, not just length: the 0.5-amplitude mic sine
                    // must reach the mic stem and the mix; the system stem
                    // stays silent (no loop leg in mic-only mode).
                    Assert.True(PeakOfWavData(Path.Combine(dir, "take-mic.wav")) > 0.25f);
                    Assert.True(PeakOfWavData(Path.Combine(dir, "take-mix.wav")) > 0.25f);
                    Assert.True(PeakOfWavData(Path.Combine(dir, "take-system.wav")) < 1e-6f);
                }
                finally
                {
                    cts.Cancel();
                    try { await emit; } catch (OperationCanceledException) { }
                }
            }
            finally
            {
                try { Directory.Delete(dir, recursive: true); } catch { }
            }
        }

        // Scans the 'data' chunk of our own float32 WAVs (plain or RF64: the
        // chunk-size field is ignored and floats are read to EOF). Length
        // asserts alone would pass on a file of zeros — this proves the
        // mixer wrote audible energy.
        private static float PeakOfWavData(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            int data = 0;
            for (int i = 0; i + 8 <= bytes.Length; i++)
            {
                if (bytes[i] == (byte)'d' && bytes[i + 1] == (byte)'a'
                    && bytes[i + 2] == (byte)'t' && bytes[i + 3] == (byte)'a')
                {
                    data = i + 8; // skip 'data' + size field
                    break;
                }
            }
            Assert.True(data > 0, $"no data chunk in {path}");
            float peak = 0f;
            for (int i = data; i + 4 <= bytes.Length; i += 4)
            {
                float a = Math.Abs(BitConverter.ToSingle(bytes, i));
                if (a > peak) peak = a;
            }
            return peak;
        }

        [Theory]
        [InlineData(0, 0, 0, 0, false)]    // fully quiet: stop at once
        [InlineData(0, 0, 1, 0, false)]    // interpolator residue: never consumable, must not block stop
        [InlineData(0, 0, 8, 0, false)]    // residue allowance bound
        [InlineData(0, 0, 9, 0, true)]     // real backlog: keep draining
        [InlineData(1, 0, 0, 0, true)]     // loop blocks still queued
        [InlineData(0, 1, 0, 0, true)]     // mic blocks still queued
        [InlineData(0, 0, 0, 2, true)]     // loop FIFO still holds samples
        public void ShouldKeepDraining_ClassifiesDrainState(
            int loopQ, int micQ, int micAvailFrames, int loopFifoFloats, bool expected)
        {
            var method = typeof(RecorderEngine).GetMethod("ShouldKeepDraining",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            Assert.NotNull(method);
            object? result = method!.Invoke(null, new object[] { loopQ, micQ, micAvailFrames, loopFifoFloats });
            Assert.Equal(expected, Assert.IsType<bool>(result));
        }

        // Pumps scripted sine blocks on both legs sharing one synthetic
        // 100-ns clock, like a real backend's capture threads.
        private static async Task EmitSineAsync(FakeCaptureFactory factory, System.Threading.CancellationToken ct)
        {
            const int frames = 480;
            long step = frames * 10_000_000L / 48000;
            long qpc = 0;
            double phaseLoop = 0, phaseMic = 0;
            while (!ct.IsCancellationRequested)
            {
                var loop = new float[frames * 2];
                var mic = new float[frames * 2];
                for (int i = 0; i < frames; i++)
                {
                    float s = (float)(0.5 * Math.Sin(phaseLoop));
                    phaseLoop += 2 * Math.PI * 440 / 48000;
                    loop[i * 2] = s; loop[i * 2 + 1] = s;
                    float m = (float)(0.5 * Math.Sin(phaseMic));
                    phaseMic += 2 * Math.PI * 880 / 48000;
                    mic[i * 2] = m; mic[i * 2 + 1] = m;
                }
                qpc += step;
                factory.Loop.Emit(loop, frames, qpc);
                factory.Mic.Emit(mic, frames, qpc);
                await Task.Delay(5, ct);
            }
        }
    }
}
