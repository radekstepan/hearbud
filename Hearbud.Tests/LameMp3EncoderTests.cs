// Guards the macOS LAME MP3 backend. The encode test runs only on macOS
// (it needs the bundled libmp3lame dylib); input-validation throws before
// any native call, so those run on every OS. Deterministic, no timing asserts.

using System;
using System.IO;
using NAudio.Wave;
using Xunit;

namespace Hearbud.Tests
{
    public sealed class LameMp3EncoderTests
    {
        [Fact]
        public void EncodeMp3_MissingInput_ThrowsFileNotFound()
        {
            var encoder = new LameMp3Encoder();
            Assert.Throws<FileNotFoundException>(() => encoder.EncodeMp3(
                Path.Combine(Path.GetTempPath(), $"hearbud-nope-{Guid.NewGuid():N}.wav"),
                Path.Combine(Path.GetTempPath(), "x.mp3"), 192));
        }

        [Fact]
        public void EncodeMp3_EmptyOutputPath_ThrowsArgument()
        {
            // Input must exist: the encoder validates the input first.
            string dir = Path.Combine(Path.GetTempPath(), $"hearbud-lame-test-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
                string wav = Path.Combine(dir, "sine.wav");
                WriteSineWav(wav, seconds: 1);
                var encoder = new LameMp3Encoder();
                Assert.Throws<ArgumentException>(() => encoder.EncodeMp3(wav, "", 192));
            }
            finally
            {
                try { Directory.Delete(dir, recursive: true); } catch { }
            }
        }

        [Fact]
        public void EncodeMp3_SineWav_ProducesMp3()
        {
            if (!OperatingSystem.IsMacOS()) return; // needs the bundled dylib

            string dir = Path.Combine(Path.GetTempPath(), $"hearbud-lame-test-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
                string wav = Path.Combine(dir, "sine.wav");
                string mp3 = Path.Combine(dir, "sine.mp3");
                WriteSineWav(wav, seconds: 1);

                var encoder = new LameMp3Encoder();
                Assert.True(encoder.EncodeMp3(wav, mp3, 192));
                Assert.True(File.Exists(mp3));
                long len = new FileInfo(mp3).Length;
                // 192 kbps CBR ≈ 24 KB/s plus headers; keep the band wide.
                Assert.InRange(len, 15_000, 35_000);
            }
            finally
            {
                try { Directory.Delete(dir, recursive: true); } catch { }
            }
        }

        private static void WriteSineWav(string path, int seconds)
        {
            using var writer = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(48000, 2));
            var block = new float[4800 * 2];
            double phase = 0;
            for (int s = 0; s < seconds * 10; s++)
            {
                for (int i = 0; i < 4800; i++)
                {
                    float v = (float)(0.5 * Math.Sin(phase));
                    phase += 2 * Math.PI * 440 / 48000;
                    block[i * 2] = v;
                    block[i * 2 + 1] = v;
                }
                writer.WriteSamples(block, 0, block.Length);
            }
        }
    }
}
