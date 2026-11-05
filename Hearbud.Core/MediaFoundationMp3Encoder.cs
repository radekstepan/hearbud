// Windows MP3 backend: NAudio Media Foundation, moved verbatim out of RecorderEngine.
// Owns the float-mix → 16-bit PCM → MP3 conversion so the engine only ever
// sees the IAudioEncoder seam.

using System.IO;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Hearbud
{
    public sealed class MediaFoundationMp3Encoder : IAudioEncoder
    {
        public bool EncodeMp3(string mixWavPath, string mp3Path, int bitrateKbps)
        {
            using var reader = new WaveFileReader(mixWavPath);
            // MF wants 16-bit PCM: float mix → samples → clipped 16-bit.
            var pcm16 = new SampleToWaveProvider16(new WaveToSampleProvider(reader));
            MediaFoundationEncoder.EncodeToMp3(pcm16, mp3Path, bitrateKbps * 1000);
            return File.Exists(mp3Path) && new FileInfo(mp3Path).Length > 0;
        }
    }
}
