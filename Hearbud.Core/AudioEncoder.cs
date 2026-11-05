// The encoding seam: everything the engine needs from an MP3 backend.
// The engine owns progress events, skip paths, and failure semantics; the
// backend only converts one finished mix WAV to MP3.

namespace Hearbud
{
    // Encodes a finished mix WAV to MP3. Returns true when the output exists
    // and is non-empty; throws on failure (the engine logs it gracefully).
    public interface IAudioEncoder
    {
        bool EncodeMp3(string mixWavPath, string mp3Path, int bitrateKbps);
    }

    public static class AudioEncoder
    {
        // Both backends live in Core, so the default switches by OS here
        // (unlike capture, where the macOS factory lives in Hearbud.Mac and
        // hosts pick it explicitly). Only the taken branch is constructed.
        public static IAudioEncoder Default { get; } =
            OperatingSystem.IsMacOS() ? new LameMp3Encoder() : new MediaFoundationMp3Encoder();
    }
}
