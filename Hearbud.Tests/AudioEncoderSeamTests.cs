// Guards the encoding seam: the engine must drive any IAudioEncoder,
// not just Media Foundation. Fully deterministic — no audio hardware,
// no timing asserts, no real MP3 encoding.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Xunit;

namespace Hearbud.Tests
{
    internal sealed class FakeEncoder : IAudioEncoder
    {
        public readonly List<(string Mix, string Mp3, int Kbps)> Calls = new();
        public bool Result = true;

        public bool EncodeMp3(string mixWavPath, string mp3Path, int bitrateKbps)
        {
            Calls.Add((mixWavPath, mp3Path, bitrateKbps));
            return Result;
        }
    }

    public sealed class AudioEncoderSeamTests
    {
        private static string NewTempDir()
        {
            var dir = Path.Combine(Path.GetTempPath(), $"hearbud-enc-test-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            return dir;
        }

        [Fact]
        public async Task StopAsync_InvokesEncoder_WithMixPathAndBitrate()
        {
            var dir = NewTempDir();
            try
            {
                var factory = new FakeCaptureFactory();
                var encoder = new FakeEncoder();
                using var engine = new RecorderEngine(factory, encoder);

                var progress = new List<int>();
                engine.EncodingProgress += (_, p) => progress.Add(p);

                await engine.StartAsync(new RecorderStartOptions
                {
                    OutputPath = Path.Combine(dir, "rec"),
                    LoopbackDeviceId = "loop-id",
                    MicDeviceId = "mic-id",
                    Mp3BitrateKbps = 192,
                });
                await engine.StopAsync();

                Assert.Single(encoder.Calls);
                var (mix, mp3, kbps) = encoder.Calls[0];
                Assert.Equal(192, kbps);
                Assert.EndsWith("-mix.wav", mix, StringComparison.OrdinalIgnoreCase);
                Assert.EndsWith(".mp3", mp3, StringComparison.OrdinalIgnoreCase);
                Assert.Equal(new[] { 0, 100 }, progress);
            }
            finally
            {
                try { Directory.Delete(dir, recursive: true); } catch { }
            }
        }

        [Fact]
        public async Task StopAsync_BitrateZero_SkipsEncoder()
        {
            var dir = NewTempDir();
            try
            {
                var factory = new FakeCaptureFactory();
                var encoder = new FakeEncoder();
                using var engine = new RecorderEngine(factory, encoder);

                var progress = new List<int>();
                engine.EncodingProgress += (_, p) => progress.Add(p);

                await engine.StartAsync(new RecorderStartOptions
                {
                    OutputPath = Path.Combine(dir, "rec"),
                    LoopbackDeviceId = "loop-id",
                    MicDeviceId = "mic-id",
                    Mp3BitrateKbps = 0,
                });
                await engine.StopAsync();

                Assert.Empty(encoder.Calls);
                Assert.Empty(progress);
            }
            finally
            {
                try { Directory.Delete(dir, recursive: true); } catch { }
            }
        }
    }
}
